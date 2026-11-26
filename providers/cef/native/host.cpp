#include "protocol.h"
#include "renderer.h"
#include "include/cef_browser.h"
#include "include/cef_client.h"
#include "include/cef_image.h"
#include "include/cef_parser.h"
#include "include/cef_resource_handler.h"
#include "include/cef_task.h"
#include "include/cef_version.h"
#include "include/views/cef_browser_view.h"
#include "include/views/cef_browser_view_delegate.h"
#include "include/views/cef_fill_layout.h"
#include "include/views/cef_window.h"
#include "include/views/cef_window_delegate.h"
#include <algorithm>
#include <cctype>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <filesystem>
#include <memory>
#include <optional>
#include <set>
#ifdef _WIN32
#include "include/cef_sandbox_win.h"
#endif

namespace infini {
class Task final : public CefTask {
 public:
  explicit Task(std::function<void()> work) : work_(std::move(work)) {}
  void Execute() noexcept override {
    try { work_(); } catch (...) { Diagnostic("native task failed"); }
  }
 private:
  std::function<void()> work_;
  IMPLEMENT_REFCOUNTING(Task);
};
void Post(cef_thread_id_t thread, std::function<void()> work) {
  CefPostTask(thread, new Task(std::move(work)));
}
bool FragmentNavigation(const std::string& current, const std::string& target) {
  CefURLParts current_parts, target_parts;
  if (!CefParseURL(current, current_parts) || !CefParseURL(target, target_parts)) return false;
  // Compare Chromium's canonical URLs. Loading a fragment on the same base
  // document retains its context, including an identical fragment target.
  auto from = CefString(&current_parts.spec).ToString();
  auto to = CefString(&target_parts.spec).ToString();
  // LoadURL without a target fragment starts a document load, including when
  // it removes the current fragment. An explicitly empty trailing '#' retains
  // the document. Renderer-side hash removal is handled by Chromium itself.
  return to.find('#') != std::string::npos &&
      from.substr(0, from.find('#')) == to.substr(0, to.find('#'));
}
struct View {
  uint64_t id = 0, create_request = 0, navigate_request = 0;
  std::atomic<uint64_t> generation{0};
  bool closing = false, navigation_pending = true;
  bool committed = false;
  uint64_t navigation = 1, candidate = 0;
  std::string title;
  CefRect bounds;
  // Window style from Create (protocol 3). Sizes of 0 mean unconstrained.
  bool frameless = false, resizable = true, content_bounds = false;
  CefSize minimum, maximum;
  // On Linux a fixed-size window pins its minimum and maximum to the size last requested by the
  // managed host, so X11/Wayland size hints forbid user resizing yet follow programmatic bounds.
  CefSize pinned;
  // Linux: the requested bounds to return to, and pin, after a full-screen or maximized state.
  std::optional<CefRect> fixed_restore;
  struct Geometry {
    uint64_t request = 0, serial = 0;
    CefRect target, submitted, observed;
    CefSize previous_pin;
    CefRect restore;
    std::chrono::steady_clock::time_point deadline, earliest, stable;
    unsigned corrections = 0;
    bool sampled = false;
  };
  uint64_t geometry_serial = 0;
  std::optional<Geometry> geometry;
  struct Transition {
    uint64_t request = 0, serial = 0;
    int target = 0;
    std::chrono::steady_clock::time_point deadline, stable;
    bool sampled = false;
  };
  uint64_t transition_serial = 0;
  std::optional<Transition> transition;
  CefRefPtr<CefBrowser> browser;
  CefRefPtr<CefBrowserView> browser_view;
  CefRefPtr<CefWindow> window;
  std::vector<Packet> close_requests;
};
class Resource;
// Defense in depth: app resources may fetch only the same managed authority. The managed host can
// additionally allow inline style attributes (style-src-attr only: no <style> elements, no external
// stylesheets) for generated markup that positions elements through style="..." attributes.
std::string ContentSecurityPolicy(bool inline_style_attributes) {
  std::string policy = "default-src 'self' data: blob:; script-src 'self' 'unsafe-inline' 'unsafe-eval'; ";
  if (inline_style_attributes) policy += "style-src-attr 'unsafe-inline'; ";
  return policy + "connect-src 'self'; frame-src 'self'; object-src 'none'; base-uri 'self'; form-action 'none'";
}
struct Resources {
  Transport* transport;
  std::mutex mutex;
  std::map<uint64_t, CefRefPtr<Resource>> entries;
  size_t leased = 0;
  uint64_t next = RandomId();
  const std::string policy;
  Resources(Transport* t, bool inline_style_attributes)
      : transport(t), policy(ContentSecurityPolicy(inline_style_attributes)) {}
  uint64_t Add(CefRefPtr<Resource> resource);
  bool Reserve(size_t bytes);
  void Release(uint64_t id, size_t bytes);
  void CancelView(uint64_t view);
  void Complete(const Packet& packet);
};
class Resource final : public CefResourceHandler {
 public:
  static std::string Trim(const std::string& value) {
    const auto first = value.find_first_not_of(" \t");
    if (first == std::string::npos) return {};
    return value.substr(first, value.find_last_not_of(" \t") - first + 1);
  }
  static std::string Lower(std::string value) {
    for (auto& c : value) if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    return value;
  }
  // RFC 9110 token characters only.
  static bool Token(const std::string& value) {
    if (value.empty() || value.size() > 64) return false;
    for (unsigned char c : value)
      if (!(std::isalnum(c) || (c != 0 && std::strchr("!#$%&'*+-.^_`|~", c)))) return false;
    return true;
  }
  static bool MediaType(const std::string& value) {
    const auto slash = value.find('/');
    return slash != std::string::npos && Token(value.substr(0, slash)) && Token(value.substr(slash + 1));
  }
  Resource(std::shared_ptr<Resources> resources, std::shared_ptr<View> view)
      : resources_(resources), view_(std::move(view)),
        policy_(resources ? resources->policy : ContentSecurityPolicy(false)) {}
  bool Open(CefRefPtr<CefRequest> request, bool& handle_request,
            CefRefPtr<CefCallback> callback) noexcept override {
    try {
      std::lock_guard lock(mutex_);
      auto resources = resources_.lock();
      handle_request = true;
      if (!resources || !AppUrl(request->GetURL()) ||
          (request->GetMethod() != "GET" && request->GetMethod() != "HEAD")) return false;
      if (cancelled_) return false;
      id_ = resources->Add(this);
      if (!id_) return false;
      generation_ = view_->generation.load();
      head_ = request->GetMethod() == "HEAD";
      callback_ = callback;
      Packet packet{103, id_, view_->id, generation_};
      String(packet.body, request->GetMethod()); String(packet.body, request->GetURL());
      handle_request = false;
      resources->transport->Send(std::move(packet));
      return true;
    } catch (...) { handle_request = true; return false; }
  }
  void GetResponseHeaders(CefRefPtr<CefResponse> response, int64_t& length,
                          CefString& redirect_url) noexcept override {
    std::lock_guard lock(mutex_);
    // InfiniFrame reports a full Content-Type value ("text/html; charset=utf-8"). CEF takes the media
    // type and the charset separately and, with nosniff, renders an unrecognised media type that still
    // carries parameters as plain text, so split them here.
    std::string type = mime_, charset;
    if (const auto semicolon = type.find(';'); semicolon != std::string::npos) {
      std::string parameters = type.substr(semicolon + 1);
      type.erase(semicolon);
      size_t start = 0;
      while (start <= parameters.size()) {
        const auto end = std::min(parameters.find(';', start), parameters.size());
        std::string parameter = parameters.substr(start, end - start);
        start = end + 1;
        const auto equals = parameter.find('=');
        if (equals == std::string::npos) continue;
        std::string name = Trim(parameter.substr(0, equals)), value = Trim(parameter.substr(equals + 1));
        if (value.size() >= 2 && value.front() == '"' && value.back() == '"') value = value.substr(1, value.size() - 2);
        if (Lower(name) == "charset" && Token(value)) charset = Lower(value);
      }
    }
    type = Lower(Trim(type));
    if (!MediaType(type)) { type = "application/octet-stream"; charset.clear(); }
    response->SetStatus(status_); response->SetMimeType(type);
    if (!charset.empty()) response->SetCharset(charset);
    response->SetHeaderByName("X-Content-Type-Options", "nosniff", true);
    response->SetHeaderByName("Cache-Control", "no-store", true);
    response->SetHeaderByName("Content-Length", std::to_string(content_length_), true);
    response->SetHeaderByName("Content-Security-Policy", policy_, true);
    length = static_cast<int64_t>(content_length_);
  }
  bool Read(void* output, int requested, int& read, CefRefPtr<CefResourceReadCallback> callback) noexcept override {
    std::lock_guard lock(mutex_);
    read = 0;
    if (cancelled_ || requested <= 0 || offset_ >= data_.size()) { FinishLocked(); return false; }
    auto count = std::min<size_t>(static_cast<size_t>(requested), data_.size() - offset_);
    std::memcpy(output, data_.data() + offset_, count);
    offset_ += count; read = static_cast<int>(count);
    if (offset_ == data_.size()) FinishLocked();
    return true;
  }
  bool Skip(int64_t count, int64_t& skipped, CefRefPtr<CefResourceSkipCallback> callback) noexcept override {
    std::lock_guard lock(mutex_);
    skipped = 0;
    if (count < 0 || cancelled_) return false;
    skipped = std::min<int64_t>(count, static_cast<int64_t>(data_.size() - offset_));
    offset_ += static_cast<size_t>(skipped);
    if (offset_ == data_.size()) FinishLocked();
    return skipped > 0;
  }
  void Cancel() noexcept override {
    try {
      std::lock_guard lock(mutex_);
      if (cancelled_) return;
      cancelled_ = true;
      auto callback = std::move(callback_);
      auto resources = resources_.lock();
      if (resources && callback && id_) resources->transport->Send(Packet{107, id_, view_->id, generation_});
      FinishLocked();
      if (callback) Post(TID_IO, [callback] { callback->Cancel(); });
    } catch (...) { Diagnostic("resource cancellation failed"); }
  }
  void Accept(const Packet& packet, int status, std::string mime, Bytes data) {
    std::lock_guard lock(mutex_);
    if (cancelled_ || !callback_ || packet.view != view_->id || packet.generation != generation_) {
      Diagnostic("ignored stale resource response"); return;
    }
    auto resources = resources_.lock();
    if (!resources || !resources->Reserve(data.size())) {
      status = 503; mime = "text/plain"; data.clear();
    }
    lease_ = data.size(); content_length_ = data.size();
    status_ = status; mime_ = std::move(mime); data_ = std::move(data);
    auto callback = std::move(callback_);
    if (head_ || data_.empty()) FinishLocked();
    Post(TID_IO, [callback] { callback->Continue(); });
  }
  uint64_t ViewId() const { return view_->id; }
 private:
  void FinishLocked() {
    if (id_) {
      auto resources = resources_.lock();
      if (resources) resources->Release(id_, lease_);
      id_ = 0; lease_ = 0;
    }
    Bytes{}.swap(data_); offset_ = 0;
  }
  std::weak_ptr<Resources> resources_;
  std::shared_ptr<View> view_;
  const std::string policy_;
  std::mutex mutex_;
  uint64_t id_ = 0, generation_ = 0;
  size_t lease_ = 0, offset_ = 0, content_length_ = 0;
  int status_ = 503;
  std::string mime_ = "text/plain";
  bool cancelled_ = false, head_ = false;
  Bytes data_;
  CefRefPtr<CefCallback> callback_;
  IMPLEMENT_REFCOUNTING(Resource);
};
uint64_t Resources::Add(CefRefPtr<Resource> resource) {
  std::lock_guard lock(mutex);
  if (entries.size() >= 16) return 0;
  if (!++next) ++next;
  entries.emplace(next, resource); return next;
}
bool Resources::Reserve(size_t bytes) {
  std::lock_guard lock(mutex);
  if (bytes > kMaxFrame || bytes > kMaxQueuedBytes - leased) return false;
  leased += bytes; return true;
}
void Resources::Release(uint64_t id, size_t bytes) {
  std::lock_guard lock(mutex);
  leased -= bytes; entries.erase(id);
}
void Resources::CancelView(uint64_t view) {
  std::vector<CefRefPtr<Resource>> cancel;
  {
    std::lock_guard lock(mutex);
    for (const auto& [id, resource] : entries)
      if (!view || resource->ViewId() == view) cancel.push_back(resource);
  }
  for (auto& resource : cancel) resource->Cancel();
}
void Resources::Complete(const Packet& packet) {
  CefRefPtr<Resource> resource;
  { std::lock_guard lock(mutex); auto it = entries.find(packet.request); if (it != entries.end()) resource = it->second; }
  if (!resource) { Diagnostic("ignored late resource response"); return; }
  Reader r{packet.body};
  int status = r.Int(); auto mime = r.String(); auto data = r.Blob();
  if (!r.Done() || status < 200 || status > 599 || mime.empty() || mime.size() > 256 ||
      mime.find_first_of("\r\n") != std::string::npos || mime.find('\0') != std::string::npos) {
    Diagnostic("invalid resource response");
    // A live malformed response must finish the request and release its slot.
    // Accept still verifies view/generation, so a stale or mismatched response
    // cannot cancel another document's live request.
    resource->Accept(packet, 502, "text/plain", {}); return;
  }
  resource->Accept(packet, status, std::move(mime), std::move(data));
}
class Host;
class Client final : public CefClient, public CefLifeSpanHandler, public CefRequestHandler,
                     public CefResourceRequestHandler, public CefJSDialogHandler,
                     public CefPermissionHandler, public CefDownloadHandler, public CefContextMenuHandler,
                     public CefLoadHandler, public CefDialogHandler {
 public:
  Client(Host* host, std::shared_ptr<View> view, std::shared_ptr<Resources> resources)
      : host_(host), view_(std::move(view)), resources_(std::move(resources)) {}
  CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
  CefRefPtr<CefRequestHandler> GetRequestHandler() override { return this; }
  CefRefPtr<CefJSDialogHandler> GetJSDialogHandler() override { return this; }
  CefRefPtr<CefPermissionHandler> GetPermissionHandler() override { return this; }
  CefRefPtr<CefDownloadHandler> GetDownloadHandler() override { return this; }
  CefRefPtr<CefContextMenuHandler> GetContextMenuHandler() override { return this; }
  CefRefPtr<CefLoadHandler> GetLoadHandler() override { return this; }
  CefRefPtr<CefDialogHandler> GetDialogHandler() override { return this; }
  bool OnFileDialog(CefRefPtr<CefBrowser>, FileDialogMode, const CefString&, const CefString&,
      const std::vector<CefString>&, const std::vector<CefString>&, const std::vector<CefString>&,
      CefRefPtr<CefFileDialogCallback> callback) noexcept override { callback->Cancel(); return true; }
  void OnLoadStart(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame, TransitionType) noexcept override;
  void OnLoadError(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, ErrorCode, const CefString&, const CefString&) noexcept override;
  void OnAfterCreated(CefRefPtr<CefBrowser> browser) noexcept override;
  void OnBeforeClose(CefRefPtr<CefBrowser> browser) noexcept override;
  bool DoClose(CefRefPtr<CefBrowser> browser) noexcept override { return false; }
  bool OnBeforePopup(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, int, const CefString&, const CefString&,
      cef_window_open_disposition_t, bool, const CefPopupFeatures&, CefWindowInfo&, CefRefPtr<CefClient>&,
      CefBrowserSettings&, CefRefPtr<CefDictionaryValue>&, bool*) noexcept override { return true; }
  bool OnOpenURLFromTab(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, const CefString&,
                       cef_window_open_disposition_t, bool) noexcept override { return true; }
  bool OnBeforeBrowse(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame, CefRefPtr<CefRequest> request,
                      bool, bool) noexcept override;
  CefRefPtr<CefResourceRequestHandler> GetResourceRequestHandler(
      CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, CefRefPtr<CefRequest>, bool, bool,
      const CefString&, bool& disable) noexcept override { disable = true; return this; }
  ReturnValue OnBeforeResourceLoad(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>,
      CefRefPtr<CefRequest> request, CefRefPtr<CefCallback>) noexcept override {
    try { return AppUrl(request->GetURL()) ? RV_CONTINUE : RV_CANCEL; } catch (...) { return RV_CANCEL; }
  }
  CefRefPtr<CefResourceHandler> GetResourceHandler(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>,
      CefRefPtr<CefRequest> request) noexcept override {
    try { if (AppUrl(request->GetURL())) return new Resource(resources_, view_); } catch (...) {}
    return nullptr;
  }
  void OnProtocolExecution(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, CefRefPtr<CefRequest>,
                           bool& allow) noexcept override { allow = false; }
  bool OnProcessMessageReceived(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
      CefProcessId source, CefRefPtr<CefProcessMessage> message) noexcept override;
  void OnRenderProcessTerminated(CefRefPtr<CefBrowser>, TerminationStatus, int, const CefString&) noexcept override;
  bool OnJSDialog(CefRefPtr<CefBrowser>, const CefString&, JSDialogType, const CefString&, const CefString&,
      CefRefPtr<CefJSDialogCallback>, bool& suppress) noexcept override { suppress = true; return false; }
  bool OnBeforeUnloadDialog(CefRefPtr<CefBrowser>, const CefString&, bool,
      CefRefPtr<CefJSDialogCallback> callback) noexcept override { callback->Continue(true, CefString()); return true; }
  bool OnRequestMediaAccessPermission(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, const CefString&, uint32_t,
      CefRefPtr<CefMediaAccessCallback> callback) noexcept override { callback->Cancel(); return true; }
  bool OnShowPermissionPrompt(CefRefPtr<CefBrowser>, uint64_t, const CefString&, uint32_t,
      CefRefPtr<CefPermissionPromptCallback> callback) noexcept override {
    callback->Continue(CEF_PERMISSION_RESULT_DENY); return true;
  }
  bool OnBeforeDownload(CefRefPtr<CefBrowser>, CefRefPtr<CefDownloadItem>, const CefString&,
      CefRefPtr<CefBeforeDownloadCallback>) noexcept override { return true; }
  void OnDownloadUpdated(CefRefPtr<CefBrowser>, CefRefPtr<CefDownloadItem>,
      CefRefPtr<CefDownloadItemCallback> callback) noexcept override { callback->Cancel(); }
  void OnBeforeContextMenu(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>,
      CefRefPtr<CefContextMenuParams>, CefRefPtr<CefMenuModel> menu) noexcept override { menu->Clear(); }
 private:
  Host* host_;
  std::shared_ptr<View> view_;
  std::shared_ptr<Resources> resources_;
  IMPLEMENT_REFCOUNTING(Client);
};
class BrowserDelegate final : public CefBrowserViewDelegate {
 public:
  cef_runtime_style_t GetBrowserRuntimeStyle() noexcept override { return CEF_RUNTIME_STYLE_ALLOY; }
 private:
  IMPLEMENT_REFCOUNTING(BrowserDelegate);
};
class WindowDelegate final : public CefWindowDelegate {
 public:
  explicit WindowDelegate(std::shared_ptr<View> view) : view_(std::move(view)) {}
  void OnWindowCreated(CefRefPtr<CefWindow> window) noexcept override {
    // Creation callbacks may run before CreateTopLevelWindow returns. Keep
    // ownership visible to the create transaction if its return wrapper fails.
    view_->window = window;
    window->SetTitle(view_->title); window->SetToFillLayout();
    window->AddChildView(view_->browser_view); window->Show();
  }
  CefRect GetInitialBounds(CefRefPtr<CefWindow>) noexcept override { return view_->bounds; }
  bool IsFrameless(CefRefPtr<CefWindow>) noexcept override { return view_->frameless; }
#ifdef _WIN32
  // Without WS_THICKFRAME: no resize border, and programmatic SetWindowPos still resizes.
  bool CanResize(CefRefPtr<CefWindow>) noexcept override { return view_->resizable; }
#else
  // X11/Wayland: a non-resizable views window publishes its current size as fixed size hints, and
  // the window manager then refuses programmatic resizing as well. A fixed-size window therefore
  // stays resizable for views but pins its minimum and maximum to the size the managed host last
  // requested (min == max hints: no user resizing and no maximize action).
  bool CanResize(CefRefPtr<CefWindow>) noexcept override { return true; }
#endif
  bool CanMaximize(CefRefPtr<CefWindow>) noexcept override { return view_->resizable; }
  CefSize GetMinimumSize(CefRefPtr<CefView>) noexcept override { return ContentLimit(Limit(view_->minimum)); }
  CefSize GetMaximumSize(CefRefPtr<CefView>) noexcept override { return ContentLimit(Limit(view_->maximum)); }
  bool CanClose(CefRefPtr<CefWindow>) noexcept override {
    view_->closing = true;
    return !view_->browser || view_->browser->GetHost()->TryCloseBrowser();
  }
  void OnWindowDestroyed(CefRefPtr<CefWindow>) noexcept override {
    view_->window = nullptr; view_->browser_view = nullptr;
  }
  cef_runtime_style_t GetWindowRuntimeStyle() noexcept override { return CEF_RUNTIME_STYLE_ALLOY; }
 private:
  CefSize Limit(const CefSize& limit) const noexcept {
    // Chromium marks fullscreen before sizing to the display. Windows still asks for min/max
    // tracking sizes during that resize; normal-window limits must not cap the fullscreen bounds.
    // On exit the flag clears before restoring bounds, so the original limits/pin apply again.
    if (view_->window && view_->window->IsFullscreen()) return CefSize();
#ifdef _WIN32
    return limit;
#else
    return view_->resizable ? limit : view_->pinned;
#endif
  }
  // Views applies size limits to the window's contents. Limits given for outer window bounds
  // subtract the measured frame (none for frameless windows); 0 stays unconstrained.
  CefSize ContentLimit(const CefSize& limit) noexcept {
    try {
      if (view_->content_bounds || view_->frameless || !view_->window || (!limit.width && !limit.height)) return limit;
      const auto outer = view_->window->GetBoundsInScreen(), inner = view_->window->GetClientAreaBoundsInScreen();
      const int frame_width = outer.width - inner.width, frame_height = outer.height - inner.height;
      if (frame_width < 0 || frame_height < 0 || frame_width > 1024 || frame_height > 1024) return limit;
      return CefSize(limit.width ? std::max(1, limit.width - frame_width) : 0,
                     limit.height ? std::max(1, limit.height - frame_height) : 0);
    } catch (...) { return limit; }
  }
  std::shared_ptr<View> view_;
  IMPLEMENT_REFCOUNTING(WindowDelegate);
};
class Host {
 public:
  Transport& transport;
  std::shared_ptr<Resources> resources;
  std::map<uint64_t, std::shared_ptr<View>> views;
  std::map<uint64_t, Packet> pending;
  std::map<uint64_t, std::chrono::steady_clock::time_point> eval_deadlines;
  std::map<uint64_t, size_t> ipc_leases;
  size_t ipc_bytes = 0;
  bool eval_timer = false;
  bool shutting_down = false;
  std::vector<Packet> shutdown_requests;
  explicit Host(Transport& t, bool inline_style_attributes)
      : transport(t), resources(std::make_shared<Resources>(&t, inline_style_attributes)) {}
  void Emit(uint16_t kind, uint64_t view, uint64_t generation, const std::string* value = nullptr) {
    Packet packet{kind, 0, view, generation}; if (value) String(packet.body, *value);
    transport.Send(std::move(packet));
  }
  void Reply(const Packet& command, const char* error = nullptr, const std::string* value = nullptr) {
    Packet reply{static_cast<uint16_t>(error ? 106 : 100), command.request, command.view, command.generation};
    if (error) String(reply.body, error); else if (value) String(reply.body, *value);
    pending.erase(command.request); eval_deadlines.erase(command.request);
    if (command.kind == 4) {
      auto view = views.find(command.view);
      if (view != views.end() && view->second->navigate_request == command.request)
        view->second->navigate_request = 0;
    }
    auto lease = ipc_leases.find(command.request);
    if (lease != ipc_leases.end()) { ipc_bytes -= lease->second; ipc_leases.erase(lease); }
    transport.Send(std::move(reply));
  }
  void NavigationSucceeded(const std::shared_ptr<View>& view, bool same_document) {
    auto it = pending.find(view->navigate_request);
    if (it == pending.end()) return;
    Packet reply{100, it->second.request, view->id, view->generation.load()};
    reply.body.push_back(same_document ? 1 : 0);
    pending.erase(it); eval_deadlines.erase(view->navigate_request);
    view->navigate_request = 0;
    transport.Send(std::move(reply));
  }
  void NavigationFailed(const std::shared_ptr<View>& view, const char* error) {
    auto it = pending.find(view->navigate_request);
    if (it != pending.end()) { auto command = it->second; Reply(command, error); }
  }
  static bool SameBounds(const CefRect& a, const CefRect& b) {
    return a.x == b.x && a.y == b.y && a.width == b.width && a.height == b.height;
  }
  // Bounds as the managed host sees them: the content area or the whole window.
  static CefRect Measured(const std::shared_ptr<View>& view) {
    return view->content_bounds ? view->window->GetClientAreaBoundsInScreen() : view->window->GetBoundsInScreen();
  }
  // Window bounds expected to produce `target`, from the frame insets measured now. Frameless
  // windows have none; GeometryTick corrects any remaining measured difference.
  static CefRect Submission(const std::shared_ptr<View>& view, const CefRect& target) {
    if (!view->content_bounds) return target;
    const auto outer = view->window->GetBoundsInScreen(), inner = view->window->GetClientAreaBoundsInScreen();
    const int left = inner.x - outer.x, top = inner.y - outer.y;
    const int right = outer.width - inner.width - left, bottom = outer.height - inner.height - top;
    if (left < 0 || top < 0 || right < 0 || bottom < 0 || left + right > 1024 || top + bottom > 1024) return target;
    return CefRect(target.x - left, target.y - top, target.width + left + right, target.height + top + bottom);
  }
  // Limits and bounds share one reference (window or content area); 0 leaves a dimension unconstrained.
  static bool WithinSizeLimits(const std::shared_ptr<View>& view, int width, int height) {
    return (!view->minimum.width || width >= view->minimum.width) &&
        (!view->minimum.height || height >= view->minimum.height) &&
        (!view->maximum.width || width <= view->maximum.width) &&
        (!view->maximum.height || height <= view->maximum.height);
  }
  void FinishGeometry(const std::shared_ptr<View>& view, const char* error = nullptr) {
    if (!view->geometry) return;
    const auto operation = *view->geometry;
    auto request = operation.request;
    view->geometry.reset();
    if (!view->resizable) {
      if (error) {
        view->pinned = operation.previous_pin;
#ifndef _WIN32
        // A submitted move/resize may already have changed the OS bounds. Preserve the old normal
        // target for an explicit retry/Restore, without pretending that we rolled back the WM.
        view->fixed_restore = operation.restore;
#endif
      } else {
        view->fixed_restore.reset();  // only confirmed bounds replace the saved normal geometry
      }
    }
    if (request) {
      auto it = pending.find(request);
      if (it != pending.end()) { auto command = it->second; Reply(command, error); }
    }
  }
  void GeometryTick(const std::shared_ptr<View>& view, uint64_t serial) {
    if (!view->geometry || view->geometry->serial != serial) return;
    if (view->closing || shutting_down || !view->window) {
      FinishGeometry(view, "geometry interrupted by window closure"); return;
    }
    if (view->window->IsFullscreen() || view->window->IsMinimized() || view->window->IsMaximized()) {
      FinishGeometry(view, "geometry interrupted by window state change"); return;
    }
    auto& operation = *view->geometry;
    auto now = std::chrono::steady_clock::now();
    if (now >= operation.deadline) {
      FinishGeometry(view, "window manager did not apply requested bounds within 2 seconds"); return;
    }
    auto actual = Measured(view);
    if (!operation.sampled || !SameBounds(actual, operation.observed)) {
      operation.observed = actual; operation.stable = now; operation.sampled = true;
    }
    if (now >= operation.earliest && now - operation.stable >= std::chrono::milliseconds(100)) {
      if (SameBounds(actual, operation.target)) {
        // A frameless window promises outer bounds equal to its content area.
        if (view->frameless && !SameBounds(view->window->GetBoundsInScreen(), view->window->GetClientAreaBoundsInScreen())) {
          FinishGeometry(view, "frameless window has a non-client area"); return;
        }
        FinishGeometry(view); return;
      }
      // Content-area bounds also correct the measured frame size; window bounds only the origin.
      if (operation.corrections >= 2 || (!view->content_bounds &&
          (actual.width != operation.target.width || actual.height != operation.target.height))) {
        FinishGeometry(view, "window manager constrained requested bounds"); return;
      }
      // Some X11 WMs interpret a CEF move as decorated-frame coordinates but
      // report the client origin. Correct the measured delta, not theme/frame
      // constants. A finite operation never changes the actual queried bounds.
      int64_t x = int64_t(operation.submitted.x) + operation.target.x - actual.x;
      int64_t y = int64_t(operation.submitted.y) + operation.target.y - actual.y;
      int64_t width = int64_t(operation.submitted.width) + operation.target.width - actual.width;
      int64_t height = int64_t(operation.submitted.height) + operation.target.height - actual.height;
      if (x < -100000 || x > 100000 || y < -100000 || y > 100000 ||
          width < 1 || width > 32768 || height < 1 || height > 32768) {
        FinishGeometry(view, "window manager bounds correction exceeds supported coordinates"); return;
      }
      operation.submitted = CefRect(static_cast<int>(x), static_cast<int>(y),
                                    static_cast<int>(width), static_cast<int>(height));
      ++operation.corrections; operation.sampled = false;
      operation.earliest = now + std::chrono::milliseconds(150);
      view->window->SetBounds(operation.submitted);
    }
    if (view->geometry && view->geometry->serial == serial)
      CefPostDelayedTask(TID_UI, new Task([this, view, serial] { GeometryTick(view, serial); }), 25);
  }
  void SetGeometry(const std::shared_ptr<View>& view, uint64_t request, const CefRect& bounds) {
    if (view->closing || shutting_down || !view->window) {
      auto it = pending.find(request);
      if (request && it != pending.end()) { auto command = it->second; Reply(command, "window closing"); }
      return;
    }
    // GeometryTick only accepts normal windows. Refuse before cancelling any state operation or
    // changing the saved normal bounds/pin, including while the WM is still applying a transition.
    if (view->transition || view->window->IsFullscreen() || view->window->IsMinimized() ||
        view->window->IsMaximized()) {
      auto it = pending.find(request);
      if (request && it != pending.end()) {
        auto command = it->second; Reply(command, "bounds require a normal window with no pending state change");
      }
      return;
    }
    FinishGeometry(view, "geometry operation superseded");
    if (!++view->geometry_serial) ++view->geometry_serial;
    auto now = std::chrono::steady_clock::now();
    View::Geometry operation;
    operation.request = request; operation.serial = view->geometry_serial;
    operation.target = bounds; operation.submitted = Submission(view, bounds);
    if (!view->resizable) {
      operation.previous_pin = view->pinned;
      operation.restore = view->fixed_restore.value_or(Measured(view));
      view->pinned = CefSize(bounds.width, bounds.height);
    }
    operation.deadline = now + std::chrono::seconds(2);
    operation.earliest = now + std::chrono::milliseconds(150);
    view->geometry = operation;
    view->window->SetBounds(operation.submitted);
    auto serial = operation.serial;
    CefPostDelayedTask(TID_UI, new Task([this, view, serial] { GeometryTick(view, serial); }), 25);
  }
  void FinishTransition(const std::shared_ptr<View>& view, const char* error = nullptr) {
    if (!view->transition) return;
    auto request = view->transition->request;
    view->transition.reset();
    auto it = pending.find(request);
    if (it != pending.end()) { auto command = it->second; Reply(command, error); }
  }
  bool TransitionMatches(const std::shared_ptr<View>& view, int state) {
    switch (state) {
      case 2: return view->window->IsFullscreen();
      case 3: return !view->window->IsFullscreen();
      case 4: return view->window->IsMinimized();
      case 5: return view->window->IsMaximized();
      case 6: return !view->window->IsMinimized() && !view->window->IsMaximized();
      default: return false;
    }
  }
  void TransitionTick(const std::shared_ptr<View>& view, uint64_t serial) {
    if (!view->transition || view->transition->serial != serial) return;
    if (view->closing || shutting_down || !view->window) {
      FinishTransition(view, "window state interrupted by closure"); return;
    }
    auto& operation = *view->transition;
    auto now = std::chrono::steady_clock::now();
    if (now >= operation.deadline) {
      FinishTransition(view, operation.target == 4
          ? "window manager did not report minimized state within 2 seconds; minimization may not be observable"
          : "window manager did not report requested state within 2 seconds");
      RepinFixedSize(view);  // a refused full-screen or maximize request must not leave the size released
      return;
    }
    if (TransitionMatches(view, operation.target)) {
      if (!operation.sampled) { operation.sampled = true; operation.stable = now; }
      if (now - operation.stable >= std::chrono::milliseconds(75)) {
        FinishTransition(view);
        RepinFixedSize(view);
        return;
      }
    } else operation.sampled = false;
    CefPostDelayedTask(TID_UI, new Task([this, view, serial] { TransitionTick(view, serial); }), 25);
  }
  // Linux only (see WindowDelegate::CanResize): a fixed-size window's pinned size hints would stop a
  // requested full-screen or maximized state. Release them first; X11 republishes hints only on a
  // size-changing SetBounds, hence the one-pixel step. RepinFixedSize restores the pin afterwards.
  void UnpinFixedSize(const std::shared_ptr<View>& view, int state) {
#ifndef _WIN32
    if (view->resizable || (state != 2 && state != 5)) return;
    if (!view->fixed_restore) view->fixed_restore = Measured(view);
    view->pinned = CefSize();
    if (view->window->IsFullscreen() || view->window->IsMaximized()) return;
    // Republish even if the internal pin was already released: a cancelled repin may have submitted
    // pinned hints before failure cleanup restored the previous (released) pin.
    const auto bounds = view->window->GetBoundsInScreen();
    view->window->SetBounds(CefRect(bounds.x, bounds.y, bounds.width, bounds.height + 1));
#endif
  }
  void RepinFixedSize(const std::shared_ptr<View>& view) {
    if (!view->fixed_restore || !view->window || view->closing || view->window->IsFullscreen() ||
        view->window->IsMaximized() || view->window->IsMinimized()) return;
    const auto restore = *view->fixed_restore;
    SetGeometry(view, 0, restore);  // pins the size again and returns to the requested bounds
  }
  void SetWindowState(const std::shared_ptr<View>& view, const Packet& packet, int state) {
    FinishGeometry(view, "geometry interrupted by window state change");
    FinishTransition(view, "window state operation superseded");
    if (state == 1) {
      view->window->Activate(); view->browser->GetHost()->SetFocus(true); Reply(packet); return;
    }
    UnpinFixedSize(view, state);
    if (!++view->transition_serial) ++view->transition_serial;
    View::Transition operation;
    operation.request = packet.request; operation.serial = view->transition_serial;
    operation.target = state; operation.deadline = std::chrono::steady_clock::now() + std::chrono::seconds(2);
    view->transition = operation;
    switch (state) {
      case 2: view->window->SetFullscreen(true); break;
      case 3: view->window->SetFullscreen(false); break;
      case 4: view->window->Minimize(); break;
      case 5: view->window->Maximize(); break;
      case 6: view->window->Restore(); break;
    }
    auto serial = operation.serial;
    CefPostDelayedTask(TID_UI, new Task([this, view, serial] { TransitionTick(view, serial); }), 25);
  }
  void Deadline(const Packet& packet) {
    eval_deadlines[packet.request] = std::chrono::steady_clock::now() + std::chrono::seconds(30);
    if (!eval_timer) {
      eval_timer = true;
      CefPostDelayedTask(TID_UI, new Task([this] { EvaluationTimer(); }), 1000);
    }
  }
  void EvaluationTimer() {
    std::vector<Packet> expired;
    auto now = std::chrono::steady_clock::now();
    for (auto& [id, deadline] : eval_deadlines) {
      auto it = pending.find(id);
      if (deadline <= now && it != pending.end()) expired.push_back(it->second);
    }
    for (auto& packet : expired) Reply(packet, "renderer command timed out");
    if (!expired.empty()) {
      eval_timer = false; BeginShutdown(); return;
    }
    if (!shutting_down && !eval_deadlines.empty())
      CefPostDelayedTask(TID_UI, new Task([this] { EvaluationTimer(); }), 1000);
    else eval_timer = false;
  }
  void Retire(const std::shared_ptr<View>& view) {
    uint64_t old = view->generation.exchange(0);
    if (old) Emit(108, view->id, old);
    std::vector<Packet> cancel;
    for (const auto& [id, packet] : pending)
      if (packet.view == view->id && (packet.kind == 6 || packet.kind == 5)) cancel.push_back(packet);
    for (auto& packet : cancel) Reply(packet, "CEF_DOCUMENT_RETIRED");
    resources->CancelView(view->id);
  }
  void Navigation(const std::shared_ptr<View>& view) {
    Retire(view);
    view->navigation_pending = true; view->committed = false; view->candidate = 0;
    if (!++view->navigation) ++view->navigation;
  }
  void Probe(const std::shared_ptr<View>& view) {
    if (!view->committed || !view->candidate || !view->browser || view->closing) return;
    auto message = CefProcessMessage::Create("if.confirm");
    auto args = message->GetArgumentList();
    args->SetString(0, std::to_string(view->candidate));
    args->SetString(1, std::to_string(view->navigation)); args->SetString(2, "");
    view->browser->GetMainFrame()->SendProcessMessage(PID_RENDERER, message);
  }
  void BeginShutdown() {
    if (shutting_down) return;
    shutting_down = true;
    resources->CancelView(0);
    for (auto& [id, view] : views) {
      FinishGeometry(view, "geometry interrupted by shutdown");
      FinishTransition(view, "window state interrupted by shutdown");
      view->closing = true; Retire(view);
      if (view->browser) view->browser->GetHost()->CloseBrowser(true);
    }
    FinishShutdown();
  }
  void FinishShutdown() {
    if (!shutting_down || !views.empty()) return;
    for (auto& packet : shutdown_requests) Reply(packet);
    shutdown_requests.clear();
    // Leave OnBeforeClose and native-window destruction callbacks before quitting.
    Post(TID_UI, [] { CefQuitMessageLoop(); });
  }
  void Created(const std::shared_ptr<View>& view, CefRefPtr<CefBrowser> browser) {
    view->browser = browser;
    auto it = pending.find(view->create_request);
    if (it != pending.end()) { auto packet = it->second; Reply(packet); }
    if (view->closing || shutting_down) browser->GetHost()->CloseBrowser(true);
  }
  void Closed(const std::shared_ptr<View>& view) {
    FinishGeometry(view, "geometry interrupted by window closure");
    FinishTransition(view, "window state interrupted by closure");
    NavigationFailed(view, "view closed during navigation");
    Retire(view);
    auto create = pending.find(view->create_request);
    if (create != pending.end()) { auto packet = create->second; Reply(packet, "browser closed during creation"); }
    for (auto& packet : view->close_requests) Reply(packet);
    view->close_requests.clear();
    Emit(104, view->id, 0);
    view->browser = nullptr;
    if (view->window) view->window->Close();
    view->browser_view = nullptr;
    views.erase(view->id);
    FinishShutdown();
  }
  void Receive(const std::shared_ptr<View>& view, CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
               CefProcessId source, CefRefPtr<CefProcessMessage> message) {
    if (source != PID_RENDERER || !frame->IsMain() || !view->browser ||
        !view->browser->IsSame(browser) || browser->GetMainFrame()->GetIdentifier() != frame->GetIdentifier()) {
      Diagnostic("renderer message frame mismatch"); return;
    }
    auto args = message->GetArgumentList();
    if (args->GetSize() != 3) { Diagnostic("renderer argument mismatch"); return; }
    auto generation = ParseId(args->GetString(0)); auto name = message->GetName();
    if (name == "if.document") {
      std::string url = frame->GetURL();
      if (!generation || !AppUrl(url) || args->GetString(1) != url || view->closing ||
          (!view->navigation_pending && view->generation.load() != 0)) { Diagnostic("document candidate rejected"); return; }
      view->candidate = generation; Probe(view); return;
    }
    if (name == "if.ready" || name == "if.fault") {
      if (!generation || generation != view->candidate || !view->committed || view->closing ||
          ParseId(args->GetString(1)) != view->navigation || !AppUrl(frame->GetURL()) ||
          !view->navigation_pending) return;
      if (name == "if.fault") {
        NavigationFailed(view, "renderer context initialization failed");
        Navigation(view);
        std::string reason = "renderer context initialization failed";
        Emit(105, view->id, 0, &reason); return;
      }
      view->generation = generation; view->navigation_pending = false;
      std::string url = frame->GetURL(); Emit(101, view->id, generation, &url);
      NavigationSucceeded(view, false);
      auto activate = CefProcessMessage::Create("if.activate");
      auto values = activate->GetArgumentList();
      values->SetString(0, std::to_string(generation)); values->SetString(1, ""); values->SetString(2, "");
      frame->SendProcessMessage(PID_RENDERER, activate); return;
    }
    if (!generation || generation != view->generation.load() || !AppUrl(frame->GetURL())) return;
    if (name == "if.lost") { Retire(view); view->navigation_pending = true; return; }
    if (name == "if.message") {
      if (args->GetString(1).length() > kMaxFrame) return;
      std::string value = args->GetString(1);
      if (value.size() <= kMaxFrame - 64) {
        Emit(102, view->id, generation, &value);
        auto ack = CefProcessMessage::Create("if.opaque.ack");
        auto values = ack->GetArgumentList();
        values->SetString(0, std::to_string(generation));
        values->SetString(1, args->GetString(2)); values->SetString(2, "");
        frame->SendProcessMessage(PID_RENDERER, ack);
      }
    } else if (name == "if.eval.ok" || name == "if.eval.error" || name == "if.send.ok" || name == "if.send.error") {
      uint64_t request = ParseId(args->GetString(2));
      auto it = pending.find(request);
      bool send = name == "if.send.ok" || name == "if.send.error";
      if (it == pending.end() || it->second.kind != (send ? 5 : 6) || it->second.view != view->id ||
          it->second.generation != generation) return;
      auto packet = it->second;
      if (name == "if.send.error") {
        auto reason = args->GetString(1);
        if (reason == "receive callback not registered") Reply(packet, "receive callback not registered");
        else if (reason == "receive callback failed") Reply(packet, "receive callback failed");
        else if (reason == "document context lost" || reason == "document context unavailable") Reply(packet, "CEF_DOCUMENT_RETIRED");
        else Reply(packet, "renderer submission failed");
      }
      else if (name == "if.eval.error") {
        auto reason = args->GetString(1);
        if (reason == "document context lost" || reason == "document context unavailable") Reply(packet, "CEF_DOCUMENT_RETIRED");
        else Reply(packet, "script failed or result unsupported");
      }
      else if (send) Reply(packet);
      else {
        std::string result = args->GetString(1);
        if (result.size() > kMaxFrame - 64) Reply(packet, "result too large"); else Reply(packet, nullptr, &result);
      }
    }
  }
  void Drain() {
    Packet packet;
    // Bound each UI turn as well as total outstanding requests.
    for (size_t count = 0; count < 32 && transport.Pop(packet); ++count) {
      try { Command(std::move(packet)); }
      catch (...) { Diagnostic("command processing failed"); BeginShutdown(); return; }
    }
    if (transport.FinishDrain()) Post(TID_UI, [this] { Drain(); });
  }
  void Command(Packet packet) {
    if (packet.kind == 11) { if (packet.request) resources->Complete(packet); return; }
    if (!packet.request) { BeginShutdown(); return; }
    if (pending.count(packet.request)) { BeginShutdown(); return; }
    if (pending.size() >= 256) { Reply(packet, "too many outstanding commands"); return; }
    pending.emplace(packet.request, Packet{packet.kind, packet.request, packet.view, packet.generation});
    Reader r{packet.body};
    if (packet.kind == 10) {
      if (!r.Done() || packet.view || packet.generation) { Reply(packet, "invalid shutdown"); return; }
      shutdown_requests.push_back(packet); BeginShutdown(); FinishShutdown(); return;
    }
    if (shutting_down) { Reply(packet, "host shutting down"); return; }
    if (packet.kind == 2) {
      auto title = r.String(); auto x = r.Int(), y = r.Int(), width = r.Int(), height = r.Int();
      auto url = r.String(); auto bootstrap = r.String();
      auto window_flags = static_cast<uint32_t>(r.Number(4));
      auto min_width = r.Int(), min_height = r.Int(), max_width = r.Int(), max_height = r.Int();
      if (!r.Done() || !packet.view || packet.generation || views.count(packet.view) || views.size() >= 32 ||
          title.size() > 4096 || !Bounds(x, y, width, height) || !AppUrl(url)) {
        Reply(packet, "invalid create request"); return;
      }
      if ((window_flags & ~kWindowKnownFlags) != 0) { Reply(packet, "unsupported window flags"); return; }
      if (!SizeLimit(min_width) || !SizeLimit(min_height) || !SizeLimit(max_width) || !SizeLimit(max_height) ||
          (min_width && max_width && min_width > max_width) || (min_height && max_height && min_height > max_height)) {
        Reply(packet, "invalid window size limits"); return;
      }
      auto view = std::make_shared<View>();
      view->id = packet.view; view->create_request = packet.request; view->title = std::move(title);
      view->bounds = CefRect(x, y, width, height);
      view->frameless = (window_flags & kWindowFrameless) != 0;
      view->resizable = (window_flags & kWindowFixedSize) == 0;
      view->content_bounds = (window_flags & kWindowContentBounds) != 0;
      view->minimum = CefSize(min_width, min_height); view->maximum = CefSize(max_width, max_height);
      if (!view->resizable) view->pinned = CefSize(width, height);
      if (!WithinSizeLimits(view, width, height)) { Reply(packet, "window size outside its size limits"); return; }
      views.emplace(packet.view, view);
      try {
        auto extra = CefDictionaryValue::Create(); extra->SetString("bootstrap", bootstrap);
        auto seed = RandomId();
        if (!seed || seed == UINT64_MAX) { views.erase(view->id); Reply(packet, "native generation unavailable"); return; }
        extra->SetString("generationSeed", std::to_string(seed));
        CefBrowserSettings settings;
        settings.javascript_close_windows = STATE_DISABLED;
        CefRefPtr<Client> client = new Client(this, view, resources);
        view->browser_view = CefBrowserView::CreateBrowserView(client, url, settings, extra, nullptr, new BrowserDelegate);
        if (!view->browser_view) { views.erase(view->id); Reply(packet, "browser view creation failed"); return; }
        view->window = CefWindow::CreateTopLevelWindow(new WindowDelegate(view));
        if (!view->window) {
          views.erase(view->id); view->browser_view = nullptr;
          Reply(packet, "native window creation failed");
        }
      } catch (...) {
        // No CEF browser exists before the BrowserView is attached to a
        // window. Roll back only that uncommitted case; a real window/browser
        // must remain registered for Created/Closed and BeginShutdown.
        if (!view->window && !view->browser) {
          view->browser_view = nullptr;
          views.erase(view->id);
        } else {
          view->closing = true;
        }
        throw;
      }
      return;
    }
    auto it = views.find(packet.view);
    if (packet.kind == 3) {
      if (!r.Done() || !packet.view) { Reply(packet, "invalid close"); return; }
      if (it == views.end()) { Reply(packet); return; }
      auto view = it->second;
      FinishGeometry(view, "geometry interrupted by window closure");
      FinishTransition(view, "window state interrupted by closure");
      view->close_requests.push_back(packet); view->closing = true; Retire(view);
      if (view->browser) view->browser->GetHost()->CloseBrowser(true);
      return;
    }
    if (it == views.end() || !it->second->browser || it->second->closing) {
      Reply(packet, packet.kind == 5 || packet.kind == 6 ? "CEF_DOCUMENT_RETIRED" : "unknown or closing view"); return;
    }
    auto view = it->second;
    switch (packet.kind) {
      case 4: {
        auto url = r.String();
        if (!r.Done() || !AppUrl(url)) { Reply(packet, "navigation denied"); return; }
        if (view->navigate_request) { Reply(packet, "navigation already pending"); return; }
        auto frame = view->browser->GetMainFrame();
        bool same_document = view->generation.load() && !view->navigation_pending &&
            FragmentNavigation(frame->GetURL(), url);
        view->navigate_request = packet.request;
        if (!same_document) Navigation(view);
        if (!same_document) Deadline(packet);
        frame->LoadURL(url);
        if (same_document) NavigationSucceeded(view, true);
        return;
      }
      case 5:
      case 6: {
        auto text = r.String();
        if (!r.Done()) { Reply(packet, "invalid document command"); return; }
        if (!packet.generation || packet.generation != view->generation.load()) {
          Reply(packet, "CEF_DOCUMENT_RETIRED"); return;
        }
        if (text.size() > kMaxQueuedBytes - ipc_bytes) { Reply(packet, "renderer submission queue full"); return; }
        ipc_leases.emplace(packet.request, text.size()); ipc_bytes += text.size();
        auto message = CefProcessMessage::Create(packet.kind == 5 ? "if.send" : "if.eval");
        auto args = message->GetArgumentList();
        args->SetString(0, std::to_string(packet.generation)); args->SetString(1, text);
        args->SetString(2, std::to_string(packet.request));
        view->browser->GetMainFrame()->SendProcessMessage(PID_RENDERER, message);
        Deadline(packet);
        return;
      }
      case 7: {
        auto x = r.Int(), y = r.Int(), width = r.Int(), height = r.Int();
        if (!r.Done() || !Bounds(x, y, width, height) || !view->window) { Reply(packet, "invalid bounds"); return; }
        // Moves resend the observed size, even when a window state/WM put it outside the limits.
        // Compare in the same coordinate mode as QueryState; never clamp a move into a resize.
        const auto actual = Measured(view);
        if ((width != actual.width || height != actual.height) && !WithinSizeLimits(view, width, height)) {
          Reply(packet, "bounds outside the window's size limits"); return;
        }
        SetGeometry(view, packet.request, CefRect(x, y, width, height)); return;
      }
      case 8: {
        auto title = r.String();
        if (!r.Done() || title.size() > 4096 || !view->window) { Reply(packet, "invalid title"); return; }
        view->title = title; view->window->SetTitle(title); Reply(packet); return;
      }
      case 9: {
        auto state = r.Int();
        if (!r.Done() || state < 1 || state > 6 || !view->window) { Reply(packet, "invalid state"); return; }
        SetWindowState(view, packet, state); return;
      }
      case 12: {
        if (!r.Done() || !view->window) { Reply(packet, "invalid state query"); return; }
        auto bounds = Measured(view);
        int flags = (view->window->IsFullscreen() ? 1 : 0) | (view->window->IsMinimized() ? 2 : 0) |
                    (view->window->IsMaximized() ? 4 : 0) | (view->window->IsActive() ? 8 : 0);
        Packet reply{100, packet.request, packet.view, packet.generation};
        Number(reply.body, static_cast<uint32_t>(bounds.x), 4); Number(reply.body, static_cast<uint32_t>(bounds.y), 4);
        Number(reply.body, static_cast<uint32_t>(bounds.width), 4); Number(reply.body, static_cast<uint32_t>(bounds.height), 4);
        Number(reply.body, flags, 4); String(reply.body, view->window->GetTitle());
        String(reply.body, view->browser->GetMainFrame()->GetURL());
        pending.erase(packet.request); transport.Send(std::move(reply)); return;
      }
      case 13: {
        auto png = r.Blob();
        static const uint8_t signature[] = {137,80,78,71,13,10,26,10};
        if (!r.Done() || !view->window || png.size() < 24 || png.size() > 1024 * 1024 ||
            std::memcmp(png.data(), signature, sizeof(signature)) != 0) { Reply(packet, "expected PNG icon, maximum 1 MiB"); return; }
        auto dimension = [&](size_t offset) {
          return (uint32_t(png[offset]) << 24) | (uint32_t(png[offset+1]) << 16) |
                 (uint32_t(png[offset+2]) << 8) | png[offset+3];
        };
        auto width = dimension(16), height = dimension(20);
        if (!width || !height || width > 512 || height > 512) { Reply(packet, "PNG icon dimensions must be 1..512"); return; }
        auto image = CefImage::CreateImage();
        if (!image->AddPNG(1.0f, png.data(), png.size())) { Reply(packet, "invalid or unsupported PNG icon"); return; }
        view->window->SetWindowIcon(image); view->window->SetWindowAppIcon(image);
        Reply(packet); return;
      }
      default: Reply(packet, "unsupported command"); return;
    }
  }
  static bool Bounds(int x, int y, int width, int height) {
    return x >= -100000 && x <= 100000 && y >= -100000 && y <= 100000 &&
        width >= 64 && width <= 16384 && height >= 64 && height <= 16384;
  }
  // 0 leaves a dimension unconstrained.
  static bool SizeLimit(int value) { return value >= 0 && value <= 16384; }
};
void Client::OnAfterCreated(CefRefPtr<CefBrowser> browser) noexcept {
  try { host_->Created(view_, browser); } catch (...) { Diagnostic("browser creation callback failed"); host_->BeginShutdown(); }
}
void Client::OnBeforeClose(CefRefPtr<CefBrowser>) noexcept {
  try { host_->Closed(view_); } catch (...) { Diagnostic("browser close callback failed"); }
}
bool Client::OnBeforeBrowse(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame,
                            CefRefPtr<CefRequest> request, bool, bool) noexcept {
  try {
    if (!AppUrl(request->GetURL())) return true;
    if (frame->IsMain() && !(view_->generation.load() && !view_->navigation_pending &&
        FragmentNavigation(frame->GetURL(), request->GetURL()))) host_->Navigation(view_);
    return false;
  } catch (...) { return true; }
}
void Client::OnLoadStart(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame, TransitionType) noexcept {
  try { if (frame->IsMain()) { view_->committed = true; host_->Probe(view_); } }
  catch (...) { Diagnostic("navigation commit callback failed"); }
}
void Client::OnLoadError(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame, ErrorCode code,
                         const CefString&, const CefString&) noexcept {
  try {
    if (frame->IsMain() && code != ERR_ABORTED && !view_->closing) {
      host_->NavigationFailed(view_, "main document load failed");
      host_->Retire(view_);
      std::string reason = "main document load failed"; host_->Emit(105, view_->id, 0, &reason);
    }
  } catch (...) { Diagnostic("navigation error callback failed"); }
}
bool Client::OnProcessMessageReceived(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
                                      CefProcessId source, CefRefPtr<CefProcessMessage> message) noexcept {
  try {
    if (message->GetName().ToString().compare(0, 3, "if.") != 0) return false;
    host_->Receive(view_, browser, frame, source, message); return true;
  } catch (...) { Diagnostic("browser message rejected"); return true; }
}
void Client::OnRenderProcessTerminated(CefRefPtr<CefBrowser>, TerminationStatus, int, const CefString&) noexcept {
  try {
    host_->NavigationFailed(view_, "renderer process terminated during navigation");
    host_->Navigation(view_);
    std::string reason = "renderer process terminated"; host_->Emit(105, view_->id, 0, &reason);
  } catch (...) { Diagnostic("renderer termination callback failed"); }
}
int Run(int argc, char* argv[], void* sandbox_info) {
#ifdef _WIN32
  if (!sandbox_info) { Diagnostic("sandbox bootstrap required"); return 70; }
  CefMainArgs args(GetModuleHandle(nullptr));
#else
  CefMainArgs args(argc, argv);
#endif
  CefRefPtr<RendererApp> app = new RendererApp;
  int subprocess = CefExecuteProcess(args, app, sandbox_info);
  if (subprocess >= 0) return subprocess;
  // Never interpret command-line data as transport credentials or browser options.
  if (argc != 1) { Diagnostic("browser arguments are not supported"); return 64; }
  Transport transport;
  if (!transport.Open()) { Diagnostic("anonymous redirected pipes required"); return 74; }
  Packet hello;
  if (!transport.Read(hello, 30000)) {
    if (const auto version = transport.LastRejectedVersion()) {
      // Fixed text plus the two version numbers only; never frame contents.
      std::fprintf(stderr, "InfiniFrame.Cef: protocol version mismatch: this companion implements protocol %u, "
                   "the managed provider sent protocol %u\n", static_cast<unsigned>(kProtocolVersion),
                   static_cast<unsigned>(version));
      return 76;
    }
    Diagnostic("hello failed"); return 76;
  }
  if (hello.kind != 1 || !hello.request || hello.view ||
      hello.generation || hello.body.size() < 36) { Diagnostic("hello failed"); return 76; }
  Reader reader{hello.body}; reader.offset = 32;
  auto origin = reader.String();
  auto host_options = static_cast<uint32_t>(reader.Number(4));
  const auto scale_bits = reader.Number(8);
  if (!reader.Done() || origin != "app://localhost/") { Diagnostic("invalid application origin or host options"); return 76; }
  if ((host_options & ~kHostKnownOptions) != 0) { Diagnostic("unsupported host options"); return 76; }
  RenderingOptions rendering;
  std::memcpy(&rendering.device_scale_factor, &scale_bits, sizeof(double));
  // 0 keeps the platform scale factor; otherwise a finite factor Chromium accepts for its switch.
  if (!(rendering.device_scale_factor == 0 ||
        (rendering.device_scale_factor >= 0.5 && rendering.device_scale_factor <= 4.0))) {
    Diagnostic("unsupported device scale factor"); return 76;
  }
  rendering.disable_pinch_zoom = (host_options & kHostDisablePinchZoom) != 0;
  rendering.disable_overscroll = (host_options & kHostDisableOverscroll) != 0;
  app->SetRenderingOptions(rendering);
  Packet accepted{100, hello.request, 0, 0};
  accepted.body.assign(hello.body.begin(), hello.body.begin() + 32);
  String(accepted.body, COMPANION_RID); String(accepted.body, CEF_VERSION);
  // Protocol 3: echo the applied host options so the provider can confirm them.
  Number(accepted.body, host_options, 4);
  CefSettings settings;
  settings.no_sandbox = false;
  settings.remote_debugging_port = 0;
  settings.log_severity = LOGSEVERITY_ERROR;
  settings.persist_session_cookies = false;
  settings.command_line_args_disabled = true;
#if defined(INFINIFRAME_CEF_ADDRESS_SANITIZER) && !defined(_WIN32)
  // Fixed same-source helper only; never accept a subprocess path from callers.
  std::error_code subprocess_error;
  const auto executable = std::filesystem::read_symlink("/proc/self/exe", subprocess_error);
  const auto sanitizer_subprocess_path = executable.parent_path() / "InfiniFrame.Cef.Subprocess";
  const auto subprocess_status = std::filesystem::symlink_status(sanitizer_subprocess_path, subprocess_error);
  if (subprocess_error || !sanitizer_subprocess_path.is_absolute() || !std::filesystem::is_regular_file(subprocess_status)) {
    Diagnostic("fixed sanitizer subprocess unavailable"); return 78;
  }
  CefString(&settings.browser_subprocess_path) = sanitizer_subprocess_path.string();
#endif
  std::filesystem::path base;
#ifdef _WIN32
  if (const auto* local = _wgetenv(L"LOCALAPPDATA")) base = local;
#else
  if (const auto* state = std::getenv("XDG_STATE_HOME")) base = state;
  else if (const auto* home = std::getenv("HOME")) base = std::filesystem::path(home) / ".local" / "state";
#endif
  auto profile_id = RandomId();
  if (!base.is_absolute() || !profile_id) { Diagnostic("private profile location unavailable"); return 78; }
  auto profile = base / "InfiniFrame" / "Cef" / "Profiles" / std::to_string(profile_id);
  if (!std::filesystem::create_directories(profile)) { Diagnostic("private profile creation failed"); return 78; }
  struct ProfileCleanup {
    std::filesystem::path path;
    std::filesystem::path runtime;
    ~ProfileCleanup() {
      std::error_code error;
      if (!runtime.empty()) std::filesystem::remove_all(runtime, error);
      std::filesystem::remove_all(path, error);
      if (error) {
        // Reporting must not change the best-effort cleanup or escape this destructor.
        try {
          std::fprintf(stderr, "InfiniFrame.Cef: profile cleanup failed category=%.32s error=%d\n",
                       error.category().name(), error.value());
        } catch (...) {}
      }
    }
  } cleanup{profile, {}};
#ifndef _WIN32
  std::filesystem::permissions(profile, std::filesystem::perms::owner_all);
  // Chromium's IPC socket work files need a native filesystem, independently
  // of the launch cwd. Reserve space for Chromium's names within sockaddr_un.
  char runtime_template[] = "/tmp/ifc-XXXXXX";
  const auto* runtime = ::mkdtemp(runtime_template);
  if (!runtime) {
    Diagnostic("short private runtime directory unavailable"); return 78;
  }
  cleanup.runtime = runtime;
  std::filesystem::permissions(cleanup.runtime, std::filesystem::perms::owner_all);
  if (setenv("TMPDIR", runtime, 1) != 0) {
    Diagnostic("private runtime directory setup failed"); return 78;
  }
#endif
#ifdef _WIN32
  CefString(&settings.root_cache_path) = profile.wstring();
  CefString(&settings.log_file) = (profile / "cef.log").wstring();
#else
  CefString(&settings.root_cache_path) = profile.string();
  CefString(&settings.log_file) = (profile / "cef.log").string();
#endif
  // Empty cache_path keeps cookies and browser storage in memory; the private
  // root still isolates Chromium's process singleton and auxiliary files.
  if (!CefInitialize(args, settings, app, sandbox_info)) { Diagnostic("CEF initialization failed"); return 78; }
  {
    Host host(transport, (host_options & kHostInlineStyleAttributes) != 0);
    transport.Send(std::move(accepted));
    transport.Start([&host] { Post(TID_UI, [&host] { host.Drain(); }); },
                    [&host] { Post(TID_UI, [&host] { host.BeginShutdown(); }); });
    CefRunMessageLoop();
    transport.Stop();
    host.resources->CancelView(0);
    // Release browser/window/client references before CefShutdown.
    host.views.clear(); host.pending.clear();
    CefShutdown();
  }
  // A transport failure is not an orderly end: report it to the parent (EX_IOERR) after the
  // same orderly CEF shutdown, so a supervisor can tell it from a requested exit.
  return transport.FailedAbnormally() ? 74 : 0;
}
}
#ifdef _WIN32
CEF_BOOTSTRAP_EXPORT int RunConsoleMain(int argc, char* argv[], void* sandbox_info,
                                       cef_version_info_t* version_info) {
  try { return infini::Run(argc, argv, sandbox_info); }
  catch (...) { infini::Diagnostic("native host failed"); return 70; }
}
#else
int main(int argc, char* argv[]) {
  try { return infini::Run(argc, argv, nullptr); }
  catch (...) { infini::Diagnostic("native host failed"); return 70; }
}
#endif
