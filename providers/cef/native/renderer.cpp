#include "renderer.h"
#include "include/cef_parser.h"
#include "include/cef_process_message.h"
#include "include/cef_scheme.h"
#include <charconv>
#include <chrono>
#include <cstdlib>
#include <string>
#include "include/cef_command_line.h"
#ifdef _WIN32
#include <windows.h>
#else
#include <unistd.h>
#endif

namespace infini {
RendererApp::RendererApp() : process_seed_(RandomId()) {
  if (!process_seed_) {
    process_seed_ = static_cast<uint64_t>(std::chrono::steady_clock::now().time_since_epoch().count());
#ifdef _WIN32
    process_seed_ ^= static_cast<uint64_t>(GetCurrentProcessId()) << 32;
#else
    process_seed_ ^= static_cast<uint64_t>(getpid()) << 32;
#endif
  }
}
void RendererApp::OnBeforeCommandLineProcessing(const CefString& process_type,
                                               CefRefPtr<CefCommandLine> command_line) noexcept {
  try {
    if (!process_type.empty()) return;
#ifdef _WIN32
    // A CEF log file can remain open after shutdown on Windows and prevent
    // private-profile removal. Preserve error logging through the stderr drain.
    command_line->AppendSwitchWithValue("enable-logging", "stderr");
#endif
    // The private profile must not pick up the user's Chrome external-extension
    // registrations (HKCU/HKLM Software\Google\Chrome\Extensions, or the Linux
    // external-extension JSON folders), download browser components, or run
    // background network services: the companion only serves app://localhost/.
    command_line->AppendSwitch("disable-extensions");
    command_line->AppendSwitch("disable-component-update");
    command_line->AppendSwitch("disable-background-networking");
    // Fail closed for every host name. Pages are served through the app:// scheme handler and
    // need no DNS, so no browser subsystem (network time, update checks, model downloads)
    // can reach the network even where the switches above do not cover it.
    command_line->AppendSwitchWithValue("host-resolver-rules", "MAP * ~NOTFOUND");
    // Panel rendering options requested by the managed host (Hello). Chromium forwards these
    // switches and the feature list to its renderer and GPU processes.
    if (rendering_.device_scale_factor > 0) {
      char value[32];
      const auto written = std::to_chars(value, value + sizeof(value), rendering_.device_scale_factor);
      if (written.ec == std::errc{})
        command_line->AppendSwitchWithValue("force-device-scale-factor", std::string(value, written.ptr));
    }
    if (rendering_.disable_pinch_zoom) command_line->AppendSwitch("disable-pinch");
    if (rendering_.disable_overscroll) {
      std::string features = command_line->GetSwitchValue("disable-features").ToString();
      if (!features.empty()) features += ",";
      command_line->AppendSwitchWithValue("disable-features",
          features + "ElasticOverscroll,OverscrollHistoryNavigation");
    }
  } catch (...) { Diagnostic("CEF command-line configuration failed"); }
}
bool AppUrl(const std::string& url) {
  if (url.size() > 65536 || url.find_first_of("\r\n\t\\") != std::string::npos ||
      url.find('\0') != std::string::npos || url.compare(0, 16, "app://localhost/") != 0) return false;
  CefURLParts parts;
  return CefParseURL(url, parts) && CefString(&parts.scheme) == "app" &&
      CefString(&parts.host) == "localhost" && CefString(&parts.username).empty() &&
      CefString(&parts.password).empty() && CefString(&parts.port).empty();
}
namespace {
void Message(CefRefPtr<CefFrame> frame, const char* name, uint64_t generation,
             const std::string& text = {}, const std::string& request = {}) {
  auto message = CefProcessMessage::Create(name);
  auto args = message->GetArgumentList();
  args->SetString(0, std::to_string(generation)); args->SetString(1, text);
  args->SetString(2, request);
  frame->SendProcessMessage(PID_BROWSER, message);
}
class BridgeHandler final : public CefV8Handler {
 public:
  BridgeHandler(RendererApp* app, int browser, uint64_t generation)
      : app_(app), browser_(browser), generation_(generation) {}
  bool Execute(const CefString& name, CefRefPtr<CefV8Value> object,
               const CefV8ValueList& arguments, CefRefPtr<CefV8Value>& retval,
               CefString& exception) noexcept override {
    try { return app_->Bridge(browser_, generation_, name, arguments, exception); }
    catch (...) { exception = "native bridge failed"; return true; }
  }
 private:
  RendererApp* app_;
  int browser_;
  uint64_t generation_;
  IMPLEMENT_REFCOUNTING(BridgeHandler);
};
}
void RendererApp::OnRegisterCustomSchemes(CefRawPtr<CefSchemeRegistrar> registrar) noexcept {
  registrar->AddCustomScheme("app", CEF_SCHEME_OPTION_STANDARD | CEF_SCHEME_OPTION_SECURE |
                                     CEF_SCHEME_OPTION_CORS_ENABLED | CEF_SCHEME_OPTION_FETCH_ENABLED);
}
void RendererApp::OnBrowserCreated(CefRefPtr<CefBrowser> browser, CefRefPtr<CefDictionaryValue> extra) noexcept {
  try {
    if (extra && extra->HasKey("bootstrap")) {
      browsers_[browser->GetIdentifier()] = browser;
      scripts_[browser->GetIdentifier()] = extra->GetString("bootstrap");
      generations_[browser->GetIdentifier()] = ParseId(extra->GetString("generationSeed"));
    }
  } catch (...) { Diagnostic("renderer configuration failed"); }
}
void RendererApp::OnBrowserDestroyed(CefRefPtr<CefBrowser> browser) noexcept {
  auto it = browsers_.find(browser->GetIdentifier());
  if (it == browsers_.end() || !it->second->IsSame(browser)) return;
  RetireContext(browser->GetIdentifier()); scripts_.erase(browser->GetIdentifier());
  generations_.erase(browser->GetIdentifier()); browsers_.erase(it);
}
void RendererApp::RetireContext(int browser) {
  auto it = contexts_.find(browser);
  if (it == contexts_.end()) return;
  message_bytes_ -= it->second.queued_bytes + it->second.unacked_bytes;
  message_count_ -= it->second.queued.size() + it->second.unacked.size();
  contexts_.erase(it);
}
void RendererApp::SendOpaque(Context& record, CefRefPtr<CefFrame> frame, const std::string& text) {
  if (!++message_sequence_) ++message_sequence_;
  record.unacked.emplace(message_sequence_, text.size()); record.unacked_bytes += text.size();
  Message(frame, "if.message", record.generation, text, std::to_string(message_sequence_));
}
void RendererApp::OnContextCreated(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
                                   CefRefPtr<CefV8Context> context) noexcept {
  uint64_t generation = 0;
  try {
    auto owner = browsers_.find(browser->GetIdentifier());
    if (!frame->IsMain() || !AppUrl(frame->GetURL()) || !scripts_.count(browser->GetIdentifier()) ||
        owner == browsers_.end() || !owner->second->IsSame(browser)) {
      return;
    }
    // Minted in native code from a browser-supplied random seed. No crypto
    // provider needs to be loaded after the renderer's sandbox has been lowered.
    const auto seed = generations_.at(browser->GetIdentifier());
    generation = seed && ++context_sequence_ ? ((seed + context_sequence_) ^ process_seed_) : 0;
    if (!generation && seed && ++context_sequence_) generation = (seed + context_sequence_) ^ process_seed_;
    if (!generation) { Diagnostic("renderer generation unavailable"); std::_Exit(70); }
    RetireContext(browser->GetIdentifier());
    auto& record = contexts_.emplace(browser->GetIdentifier(), Context{generation, browser, context}).first->second;
    auto json = context->GetGlobal()->GetValue("JSON");
    auto stringify = json ? json->GetValue("stringify") : nullptr;
    if (!stringify || !stringify->IsFunction()) {
      Message(frame, "if.document", generation, frame->GetURL()); return;
    }
    record.json = json; record.stringify = stringify;
    CefRefPtr<BridgeHandler> handler = new BridgeHandler(this, browser->GetIdentifier(), generation);
    auto host = CefV8Value::CreateObject(nullptr, nullptr);
    // The generated InfiniFrame adapter wraps these native entry points and
    // multiplexes subscribers. Its assignments must remain writable.
    auto property = V8_PROPERTY_ATTRIBUTE_DONTDELETE;
    host->SetValue("postData", CefV8Value::CreateFunction("postData", handler), property);
    host->SetValue("receiveCallback", CefV8Value::CreateFunction("receiveCallback", handler), property);
    auto root = CefV8Value::CreateObject(nullptr, nullptr);
    root->SetValue("host", host, property);
    context->GetGlobal()->SetValue("infiniframe", root, property);
    const auto& script = scripts_.at(browser->GetIdentifier());
    if (!script.empty()) {
      CefRefPtr<CefV8Value> result; CefRefPtr<CefV8Exception> error;
      if (!context->Eval(script, "app://localhost/__infiniframe_bootstrap.js", 1, result, error)) {
        Message(frame, "if.document", generation, frame->GetURL());
        return;
      }
    }
    // Bootstrap postData is bounded and queued until registration/activation;
    // do not announce a usable document when trusted initialization failed.
    record.bootstrap_failed = false;
    Message(frame, "if.document", record.generation, frame->GetURL());
  } catch (...) {
    Diagnostic("renderer context initialization failed");
    try {
      auto it = contexts_.find(browser->GetIdentifier());
      if (generation && it != contexts_.end() && it->second.generation == generation &&
          it->second.v8->IsSame(context)) {
        it->second.bootstrap_failed = true;
        Message(frame, "if.document", generation, frame->GetURL());
      }
    } catch (...) { Diagnostic("renderer failed-context announcement failed"); }
  }
}
void RendererApp::OnContextReleased(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
                                    CefRefPtr<CefV8Context> context) noexcept {
  try {
    auto it = contexts_.find(browser->GetIdentifier());
    if (it != contexts_.end() && it->second.browser->IsSame(browser) && it->second.v8->IsSame(context)) {
      Message(frame, "if.lost", it->second.generation);
      RetireContext(browser->GetIdentifier());
    }
  } catch (...) { Diagnostic("renderer context release failed"); }
}
bool RendererApp::Bridge(int browser, uint64_t generation, const CefString& name,
                         const CefV8ValueList& args, CefString& exception) {
  auto it = contexts_.find(browser);
  auto current = CefV8Context::GetCurrentContext();
  if (it == contexts_.end() || it->second.generation != generation || !current ||
      !current->IsSame(it->second.v8) || !current->GetFrame()->IsMain() ||
      !AppUrl(current->GetFrame()->GetURL())) { exception = "retired document"; return true; }
  if (name == "receiveCallback") {
    if (args.size() != 1 || !args[0]->IsFunction()) exception = "expected one callback";
    else it->second.receive = args[0];
    return true;
  }
  if (name == "postData") {
    if (args.size() != 1 || !args[0]->IsString()) { exception = "expected one string"; return true; }
    auto value = args[0]->GetStringValue();
    if (value.length() > kMaxFrame) { exception = "message too large"; return true; }
    auto text = value.ToString();
    if (text.size() > kMaxFrame - 64) { exception = "message too large"; return true; }
    auto& record = it->second;
    if (message_count_ >= kMaxQueuedFrames || text.size() > kMaxQueuedBytes - message_bytes_) {
      exception = "document message queue full"; return true;
    }
    ++message_count_; message_bytes_ += text.size();
    if (record.active) SendOpaque(record, current->GetFrame(), text);
    else {
      record.queued_bytes += text.size(); record.queued.push_back(std::move(text));
    }
    return true;
  }
  return false;
}
bool RendererApp::OnProcessMessageReceived(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
                                           CefProcessId source, CefRefPtr<CefProcessMessage> message) noexcept {
  try {
    auto name = message->GetName();
    if (source != PID_BROWSER || (name != "if.send" && name != "if.eval" &&
                                 name != "if.confirm" && name != "if.activate" &&
                                 name != "if.opaque.ack")) return false;
    auto args = message->GetArgumentList();
    if (!frame->IsMain() || args->GetSize() != 3) return true;
    uint64_t generation = ParseId(args->GetString(0));
    auto request = args->GetString(2).ToString();
    auto it = contexts_.find(browser->GetIdentifier());
    if (it == contexts_.end() || !it->second.browser->IsSame(browser) ||
        it->second.generation != generation || !it->second.v8->IsValid() ||
        it->second.v8->GetFrame()->GetIdentifier() != frame->GetIdentifier() || !AppUrl(frame->GetURL())) {
      if (name == "if.eval" || name == "if.send")
        Message(frame, name == "if.eval" ? "if.eval.error" : "if.send.error", generation, "document context lost", request);
      return true;
    }
    auto& record = it->second;
    if (name == "if.opaque.ack") {
      auto serial = ParseId(args->GetString(1));
      auto ack = record.unacked.find(serial);
      if (ack != record.unacked.end()) {
        message_bytes_ -= ack->second; record.unacked_bytes -= ack->second;
        --message_count_; record.unacked.erase(ack);
      }
      return true;
    }
    if (name == "if.confirm") {
      Message(frame, record.bootstrap_failed ? "if.fault" : "if.ready", generation, args->GetString(1));
      return true;
    }
    if (record.bootstrap_failed) return true;
    if (name == "if.activate") {
      if (!record.active) {
        record.active = true;
        for (const auto& text : record.queued) SendOpaque(record, frame, text);
        record.queued.clear(); record.queued_bytes = 0;
      }
      return true;
    }
    if (!record.v8->Enter()) {
      Message(frame, name == "if.send" ? "if.send.error" : "if.eval.error",
              generation, "document context unavailable", request);
      return true;
    }
    struct ExitContext { CefRefPtr<CefV8Context> context; ~ExitContext() { context->Exit(); } } exit{record.v8};
    if (name == "if.send") {
      if (!record.receive) { Message(frame, "if.send.error", generation, "receive callback not registered", request); return true; }
      CefV8ValueList values{CefV8Value::CreateString(args->GetString(1))};
      auto receive = record.receive;
      auto result = receive->ExecuteFunctionWithContext(record.v8, nullptr, values);
      if (!result || receive->HasException()) {
        receive->ClearException();
        Message(frame, "if.send.error", generation, "receive callback failed", request);
      } else Message(frame, "if.send.ok", generation, "", request);
      return true;
    }
    CefRefPtr<CefV8Value> value; CefRefPtr<CefV8Exception> error;
    if (!record.v8->Eval(args->GetString(1), frame->GetURL(), 1, value, error)) {
      Message(frame, "if.eval.error", generation, "script evaluation failed", request); return true;
    }
    if (value && value->IsPromise()) {
      Message(frame, "if.eval.error", generation, "Promise results are not supported; use postData", request); return true;
    }
    std::string result = "null";
    if (value && !value->IsUndefined()) {
      auto encoded = record.stringify->ExecuteFunctionWithContext(record.v8, record.json, {value});
      if (!encoded || record.stringify->HasException()) {
        record.stringify->ClearException();
        Message(frame, "if.eval.error", generation, "result is not JSON serializable", request); return true;
      }
      if (encoded->IsString()) {
        if (encoded->GetStringValue().length() > kMaxFrame) {
          Message(frame, "if.eval.error", generation, "result too large", request); return true;
        }
        result = encoded->GetStringValue();
      }
    }
    if (result.size() > kMaxFrame - 64) Message(frame, "if.eval.error", generation, "result too large", request);
    else Message(frame, "if.eval.ok", generation, result, request);
    return true;
  } catch (...) { Diagnostic("renderer command failed"); return true; }
}
}
