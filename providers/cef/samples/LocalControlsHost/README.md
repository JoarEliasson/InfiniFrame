# Local controls native Blazor Hybrid host

This .NET 10 executable composes `LocalControlsUi.App` with InfiniFrame Application/BlazorWebView and the external `Ceffortless` provider package through public APIs. The RCL's [integration contract](../LocalControlsUi/README.md) describes its services, local assets and lifecycle. This host starts no ASP.NET server, SignalR connection or WebAssembly runtime.

The sample supports Windows and Linux. See [ACCEPTANCE.md](ACCEPTANCE.md) for interactive runtime checks.

## Composition

`Program.cs` registers `AddLocalControlsUi()` once in the application service collection. The host's ErrorBoundary wraps the actual `LocalControlsUi.App`; `HostRoot` attaches to CSS selector `#app`. The complete external-asset document uses `<base href="/">`, the viewport meta tag and `app://localhost/`. Keep these together when adapting the host to another RCL.

There is one native window, `main`, titled **Local controls example**, at x=0/y=0 with a fixed chromeless 1024 by 600 surface. Host options explicitly allow inline **style attributes**, fix the device scale factor at 1 and disable pinch zoom/overscroll. This opt-in supports the RCL's Razor width attributes and dialog drag positions. It does not enable `<style>` elements or external stylesheets. Separate strict-default and forbidden-style-element integration cases remain outside this sample.

Web security is explicitly on. Certificate ignoring, browser permissions, filesystem access, JavaScript clipboard access, media streams/autoplay, context menu and DevTools are explicitly off. The custom user agent is empty, trust-all is off, and only the local origin is trusted. Do not replace these settings with a permissive browser-policy shortcut.

`InfiniFrameApplication.RunAsync` owns the application/window lifecycle. Ctrl+C cancels its normal token; the framework handles last-native-window close and application-stop events. The host creates no private STA, native loop, secondary window or native dialog. `await using` retains asynchronous application disposal, which also disposes the root service provider and stops the fake backend's task/streams. There is no second service provider and no backend replacement on reload.

Managed startup/companion/disposal exceptions print the full exception to stderr and return exit code 1. Requested cancellation returns normally after framework teardown. ErrorBoundary logs retain the original component exception; its fallback remains in the document. The framework's optional AppDomain native-message-dialog handler is disabled because this provider does not offer native dialogs. Normal .NET unhandled-exception behavior and console diagnostics remain available.

`host-errors.js` preserves `console.error` and forwards browser errors, unhandled rejections and console errors using the public local v2 Post envelope. It caps forwarded messages at 20 per document, each at 12,000 characters, with four queued entries and at most forty bridge retries over ten seconds. Page teardown stops the interval. These diagnostics are intentionally bounded; the host console and native/runtime logs remain relevant after the cap. Provider shutdown diagnostics are printed for both successful and failed runs. A caught component error is visible/logged; it does not itself close the application or certify a successful run.

## Build and run

Build matching packages from this checkout and select a runtime for the target platform. See [the build instructions](../../BUILD.md).

From a fresh output directory, use its apphost or a .NET 10 launcher:

```text
dotnet LocalControlsHost.dll --help
dotnet LocalControlsHost.dll
dotnet LocalControlsHost.dll --payload <verified-runtime-directory> --state-root <state-directory>
```

`--payload` selects a complete, independently verified runtime directory; the provider performs its normal payload/manifest validation. Without it, the provider uses `runtimes/<current-rid>/native/cef` beside the app. That default directory must be externally supplied; the thin package does not populate it. `ContosoCefHostDeployPayload` defaults to `false`, and explicit `false` is allowed. Old native pack paths or `true` deployment requests are rejected. Runtime executable permissions and sandbox ACLs belong to the installer. This sample neither builds nor downloads CEF and performs no fallback to another engine. `--state-root` is optional; the default is the short temporary `lc-cef` directory, under which the provider owns a unique per-run directory and cleanup. The containing root can remain empty after shutdown. Neither CLI option bypasses verification or alters browser security.

## Assets in build and publish output

The Razor SDK participates in normal static-web-assets discovery. `LocalControlsAssets.targets` additionally copies the host's physical `wwwroot` and the RCL's `wwwroot` into **both** build and publish outputs, preserving all paths. The resulting web root includes:

```text
wwwroot/index.html
wwwroot/host.css
wwwroot/host-errors.js
wwwroot/_content/LocalControlsUi/local-controls.css
wwwroot/_content/LocalControlsUi/local-controls.js
wwwroot/_content/LocalControlsUi/module.svg
wwwroot/_framework/blazor.webview.js
wwwroot/_framework/blazor.modules.json
```

The WebView package normally marks its bootstrap assets for publish, while build uses a development manifest pointing into the package cache. The host's build target selects the resolved primary WebView script and module manifest from the SDK's `StaticWebAsset` items and copies their exact bytes to the stable `_framework` routes. It fails if either required input is missing or ambiguous. This uses the declared package/SDK inputs without a machine path, package-version path or cache scan; only these two WebView files are copied. Publish retains its ordinary SDK handling.

The framework's Blazor WebView resource handler reads manifests with a physical-output fallback. The required host, RCL and bootstrap files therefore have deployed copies without depending on a development manifest's source/cache roots. No source directory is registered as a custom scheme and no hard-coded MIME handler replaces the framework. File-provider verification can check those copies without consulting any manifest; real serving/MIME/bootstrap and relocated-output acceptance still require the matching framework/runtime. Test from a new directory with no accessible source/cache manifest roots; a successful in-repository launch alone is insufficient.

## Offline checks (no host packages or native runtime)

`checks/LocalControlsHost.Checks.csproj` is a dependency-free console harness targeting the installed .NET/ASP.NET Core 10 reference packs. It compiles the real argument parser and ErrorBoundary logger and reads copied assets using a physical file provider. `AssetsOnly.proj` imports the exact deployment targets without an SDK or project build; pass `WebViewBootstrapDirectory` pointing to the already resolved WebView static inputs for that fixture. Omitting those inputs must fail, rather than silently producing an incomplete build web root. The Node harness exercises only the diagnostic-forwarder script in a synthetic context.

Run from `providers/cef` using a fresh writable output directory. The helper's offline configuration contains no package sources. Keep audit enabled when restoring the actual host and record whether its configured feeds/advisory source supplied fresh vulnerability data; successful filesystem-only restoration by itself does not prove that data was available.

```powershell
dotnet msbuild samples/LocalControlsHost/checks/AssetsOnly.proj '-t:CopyLocalControlsAssetsToBuildOutput;CopyLocalControlsAssetsToPublishOutput' '-p:WebViewBootstrapDirectory=<resolved-WebView-static-assets>' '-p:TargetDir=<absolute-output>/build/' '-p:PublishDir=<absolute-output>/publish/'
dotnet restore samples/LocalControlsHost/checks/LocalControlsHost.Checks.csproj --configfile samples/LocalControlsHost/checks/NuGet.Offline.config -p:NuGetAudit=true
dotnet run --project samples/LocalControlsHost/checks/LocalControlsHost.Checks.csproj -c Release --no-restore -- <absolute-sample-directory> <absolute-output>/build/wwwroot <absolute-output>/publish/wwwroot
node --check samples/LocalControlsHost/wwwroot/host-errors.js
node samples/LocalControlsHost/checks/host-errors.test.cjs
```

These checks do not compile `Program.cs`/Razor against InfiniFrame, execute JS interop, use `HtmlRenderer`, launch CEF or substitute for native qualification. No `--smoke` option is supplied: the manual acceptance sequence makes the unqualified work explicit.

For relocation regression, save an expected digest JSON object mapping all eight web paths above to their SHA-256 values. Obtain the bootstrap/module digests from the resolved declared WebView inputs, not from the output under test. Run the helper against an ordinary build and a separately copied build web root:

```text
dotnet run --project samples/LocalControlsHost/checks/LocalControlsHost.Checks.csproj -c Release --no-build --no-restore -- --web-roots <expected-digests.json> <build-output>/wwwroot <relocated-build>/wwwroot
```

That mode opens only the digest file and the supplied physical web roots. It never resolves source/cache manifests, so the missing bootstrap in the original build output fails the regression even when the development cache is present. Use the normal host project restore/build separately to qualify actual SDK target integration. This is file/provider evidence, not browser or MIME acceptance.
