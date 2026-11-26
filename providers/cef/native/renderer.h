#pragma once
#include "protocol.h"
#include "include/cef_app.h"
#include "include/cef_v8.h"
#include <map>

namespace infini {
bool AppUrl(const std::string& url);
// Browser-process rendering options from Hello, applied as fixed Chromium switches in
// OnBeforeCommandLineProcessing. Never derived from the command line or environment.
struct RenderingOptions {
  double device_scale_factor = 0;  // 0: the platform's scale factor
  bool disable_pinch_zoom = false;
  bool disable_overscroll = false;
};
class RendererApp final : public CefApp, public CefRenderProcessHandler {
 public:
  RendererApp();
  // Set once, after Hello and before CefInitialize.
  void SetRenderingOptions(const RenderingOptions& options) { rendering_ = options; }
  void OnBeforeCommandLineProcessing(const CefString& process_type,
                                    CefRefPtr<CefCommandLine> command_line) noexcept override;
  CefRefPtr<CefRenderProcessHandler> GetRenderProcessHandler() override { return this; }
  void OnRegisterCustomSchemes(CefRawPtr<CefSchemeRegistrar> registrar) noexcept override;
  void OnBrowserCreated(CefRefPtr<CefBrowser> browser, CefRefPtr<CefDictionaryValue> extra) noexcept override;
  void OnBrowserDestroyed(CefRefPtr<CefBrowser> browser) noexcept override;
  void OnContextCreated(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
                        CefRefPtr<CefV8Context> context) noexcept override;
  void OnContextReleased(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
                         CefRefPtr<CefV8Context> context) noexcept override;
  bool OnProcessMessageReceived(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
                               CefProcessId source, CefRefPtr<CefProcessMessage> message) noexcept override;
  bool Bridge(int browser_id, uint64_t generation, const CefString& name,
              const CefV8ValueList& arguments, CefString& exception);
 private:
  struct Context {
    uint64_t generation;
    CefRefPtr<CefBrowser> browser;
    CefRefPtr<CefV8Context> v8;
    CefRefPtr<CefV8Value> receive, json, stringify;
    bool active = false;
    bool bootstrap_failed = true;
    size_t queued_bytes = 0;
    std::vector<std::string> queued;
    size_t unacked_bytes = 0;
    std::map<uint64_t, size_t> unacked;
  };
  std::map<int, std::string> scripts_;
  std::map<int, uint64_t> generations_;
  std::map<int, CefRefPtr<CefBrowser>> browsers_;
  std::map<int, Context> contexts_;
  RenderingOptions rendering_;
  uint64_t process_seed_ = 0;
  uint64_t context_sequence_ = 0;
  uint64_t message_sequence_ = 0;
  size_t message_bytes_ = 0, message_count_ = 0;
  void RetireContext(int browser);
  void SendOpaque(Context& record, CefRefPtr<CefFrame> frame, const std::string& text);
  IMPLEMENT_REFCOUNTING(RendererApp);
};
}
