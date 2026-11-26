# Local controls Blazor Hybrid UI

A neutral .NET 10 Razor class library for a later native Blazor Hybrid host. It contains ordinary Razor components, a real `Router`, .NET fake services, an original SVG, external CSS, and a small local ES module for browser dialog/pointer primitives. It has no InfiniFrame/provider references, ASP.NET server, SignalR, WebAssembly, render modes, private WinForms API, external resources, or frontend package dependencies. `Microsoft.AspNetCore.App` supplies component/DI/JS interop contracts; a framework reference does not start a server. This sample is not packable.

The exploratory canvas informed the layout only. Its HTML, JS lifecycle simulation and query-parameter state were not copied into this runtime.

## Host registration and root

Reference `LocalControlsUi.csproj`, then register services in the **application-lived** service collection used by the hybrid renderer:

```csharp
using LocalControlsUi.Services;

services.AddLocalControlsUi();
// Configure the host's existing Blazor Hybrid renderer and IJSRuntime normally.
// Attach the public root component typeof(LocalControlsUi.App) to "#app".
```

`#app` is the recommended host selector, not a selector registered by the library. The host supplies the complete document, base URI, renderer/bootstrap script, navigation manager, logging and error boundary. A minimal host document body is `<div id="app"></div>`. Use `<base href="/">` and a normal viewport meta tag. Add this **external** stylesheet to the document head:

```html
<link rel="stylesheet" href="_content/LocalControlsUi/local-controls.css">
```

Do not register a server, endpoints or an interactive web render mode for this library. Do not construct a second singleton backend on every document reload. The root does not add an ErrorBoundary; unexpected binding failures go through `DispatchExceptionAsync` (or host logging if already disposed). Interop failures during normal operation remain visible.

## Local static assets

Serve the library's `wwwroot` files at these paths under the intended local origin `app://localhost`:

- `/_content/LocalControlsUi/local-controls.css`
- `/_content/LocalControlsUi/local-controls.js` (ES module, imported by the component)
- `/_content/LocalControlsUi/module.svg` (original artwork)

The Razor SDK emits static-web-assets manifests. A custom native host must explicitly consume that manifest or copy `wwwroot/*` into its own served `_content/LocalControlsUi/` directory in both build and publish output. Merely referencing the DLL does not prove that a custom provider serves those files. Set appropriate local MIME types (`text/css`, `text/javascript`, `image/svg+xml`). The CSS is deliberately not scoped, so no generated `*.styles.css` or runtime `<style>` injection is required. Copy/publish and module loading are host integration acceptance items.

## State and lifecycle

`AddLocalControlsUi` uses idempotent singleton registrations for `FakeControlBackend` and `FakeLoginService`. The backend retains the integer setpoint (initially 50) in memory for the lifetime of its application service provider. A process restart or provider replacement resets it. There is no browser storage, query-string state, or persistent credential store.

Each `App` creates and owns one `DocumentSession` and one bounded latest-snapshot channel subscription. The backend publishes fake readings approximately every 100 ms from one owned `PeriodicTimer` task. The timer never changes the setpoint. A slow UI gets the latest snapshot rather than an unbounded notification backlog; the backend holds channel writers, never component callbacks. The root consumes that stream through awaited `InvokeAsync` calls. It cancels and awaits its binding task, retires its subscription/session and rejects queued component updates during disposal. Navigating between `/` and `/details` retains the current root/session.

The reload button calls actual `location.reload()` through the host's `IJSRuntime`. The later host must retire/dispose the old root and JS document, create a fresh root for the replacement document, and keep the application backend alive. RCL counters should then settle at one active session and subscription, with created/retired totals increasing. The library cannot independently force or prove host document teardown. Async-dispose the application service provider on shutdown so the backend completes all streams, cancels and awaits its timer, and releases its cancellation source.

Diagnostics count only this backend instance and its RCL sessions/subscriptions. They are not measurements of actual framework dispatchers, host DI scopes, CEF processes, native windows, event listeners inside the framework, or native-close cleanup.

## Interaction behavior

- Apply validates an integer from 0 through 100 and writes it to the fake .NET backend. Readback is the backend snapshot. The indicator uses the Razor-rendered inline style `width: {value * 2}px`: 50 gives 100 px; **Step to 75** gives 150 px. Heartbeats update only the three gauges and sequence.
- Details and Back use real Blazor router navigation. Returning to Overview initializes its edit field from the retained backend value.
- Local sign-in opens an HTML `dialog` using `showModal()`. It stays inside the document. Browser modal semantics block background input, constrain focus and handle Escape; the helper restores the opener's focus. Another `dialog` supplies nested confirmation and returns focus to its opener in the parent.
- Drag the parent header. Pointer capture continues the gesture outside the header; pointerup, pointercancel, lost capture, closing, opening nested confirmation and disposal end the gesture. Positions are bounded to the viewport and written as inline style attributes. Keyboard users can use every action without dragging.
- Fake login accepts only `operator` / `demo` after a cancellable 300 ms delay. Other values reject. Closing cancels pending login, clears the password and invalidates stale results. Closing/reopening cannot let an earlier result overwrite a newer attempt. Nested confirmation records an acknowledgement only; it does not authorize a device operation.
- Repeated open requests share the pending opening before any await and become no-ops while open. The JS opening acknowledges its accepted generation; a close that arrives before that acknowledgement still retires the opening. Opening errors close any dialog already created before allowing a retry, and disposal waits for an in-flight opening before releasing interop references.
- Component disposal cancels/awaits pending fake login and module initialization, removes owned pointer/close listeners, ends capture, closes dialogs and releases JS/.NET references. An already-disconnected JS document is an expected teardown condition; live interop failures are not swallowed.

## Offline managed verification

Use SDK 10.0.402 from the provided handoff. Restore with a temporary NuGet config containing `<packageSources><clear /></packageSources>` and `-p:NuGetAudit=false`; no PackageReferences or downloads are needed. Build only this library and its new tests, not the repository solution.

```powershell
& $dotnet restore tests/LocalControlsUi.Tests/LocalControlsUi.Tests.csproj --configfile $offlineConfig -p:NuGetAudit=false
& $dotnet build samples/LocalControlsUi/LocalControlsUi.csproj -c Release --no-restore
& $dotnet run --project tests/LocalControlsUi.Tests/LocalControlsUi.Tests.csproj -c Release --no-restore
```

`LocalControlsUi.Tests` is a dependency-free executable, not a `dotnet test` adapter project. Its eleven checks cover DI singleton reuse, value retention across explicitly replaced sessions, range validation, heartbeat independence, bounded latest-value delivery, session isolation, stale-session rejection, concurrent retirement, reader completion, backend shutdown and fake-login cancellation. A static `HtmlRenderer` check creates/disposes five actual `App` roots, checks that RCL sessions/subscriptions return to zero, and checks the Razor-rendered 100/150 px style attributes with retained state. Three controlled-renderer checks run the actual dialog component and input/submit event handlers against a delayed fake JS runtime: repeated opens during import/helper creation/show acknowledgement, closing a pending login and clearing the password, stale close rejection, a close before acknowledgement, failed-opening retry and disposal during initialization. They do not execute the JS module or measure computed browser geometry. These are managed checks, not interactive/browser/native qualification.

## Manual host acceptance still required

1. Configure one fixed, chromeless 1024 × 600 client surface and explicit DPR 1 in the host. The UI has a fixed logical 1024 × 600 layout; it does not configure native window sizing, device scale or host zoom. Check actual clipping, font metrics and screenshot geometry on the target runtime.
2. Serve the host document, renderer bootstrap and three assets locally from `app://localhost` in build and publish output. Open Overview, verify the original SVG, external rules and local ES module load without external requests or console errors.
3. Under the intended default-deny policy with inline **style attributes** explicitly allowed, check computed width: initial 50 → 100 px, Step → 75 / 150 px, edit 23 → Apply → readback 23 / 46 px. Wait for heartbeat updates and confirm the setpoint stays fixed. Also check value 0 and 100.
4. Navigate Details → Back without document replacement. Open sign-in, Tab/Shift+Tab around it, try background mouse/keyboard input, accept/reject fake credentials, dismiss during the delay, reopen, drag across boundaries and exercise pointer cancellation. Open confirmation; verify only the nested dialog receives focus, Escape returns to the parent, and closing the parent restores its opener.
5. Set a non-default value and perform multiple actual document reloads. Observe increasing session IDs and balanced retired counts after teardown; confirm one active RCL session/subscription and retained value. Reload while a dialog/login is active. Check the host's independent scope/dispatcher/native diagnostics and logs for real cleanup. The RCL counters alone are insufficient proof.
6. Test normal native close and cleanup through the host. Compare strict/default CSP behavior and a separate forbidden `<style>`-element negative fixture in the host; do not weaken policy to make this UI pass. No CSSOM shim or policy mutation exists in this library. Browser permissions, external navigation, `window.open`, dense 510-widget stress and hardware gestures are outside this sample.

No interactive browser, CEF process, native window, reload integration, CSP comparison, static-asset publish integration or native-close acceptance is claimed by the standalone managed build/tests.
