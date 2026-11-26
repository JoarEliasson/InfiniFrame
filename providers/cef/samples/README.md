# CEF provider samples

Build the managed packages with [BUILD.md](../BUILD.md) before running the samples. Commands on this page use `providers/cef` as the working directory. `LocalControlsHost` accepts `--payload` for a verified native runtime. For `Contoso.CefHostConsumer`, install the complete runtime at `runtimes/<rid>/native/cef` alongside the app.

## Contoso.CefHostConsumer

These commands assume the build output is in `artifacts/cef-build`. Substitute your output directory if it differs.

A Blazor and static-page application that runs one scenario per process through the CEF provider. It drives and reads
its pages through InfiniFrame's public APIs only, and writes every check (pass or fail, with details) to a JSON report.

```sh
dotnet restore samples/Contoso.CefHostConsumer --configfile ../../artifacts/cef-build/NuGet.config --packages ../../artifacts/cef-build/packages
dotnet build samples/Contoso.CefHostConsumer -c Release --no-restore
python samples/tools/scenario_driver.py samples/Contoso.CefHostConsumer/bin/Release/net10.0/Contoso.CefHostConsumer.exe main runs state
```

`scenario_driver.py` (it needs `psutil`) starts the consumer, points the provider's state root at the given folder
(`CONSUMER_STATE_ROOT`), and acts on the consumer's markers. It records the process tree, sockets, and any state left
behind in `runs/driver-<scenario>.json`; the consumer's own report is `runs/report-<scenario>.json`. Three scenarios
need the driver: `renderer-loss` and `companion-loss` (it kills exactly the renderers or the companion of this run), and
`native-close` (it posts `WM_CLOSE` to the window; Windows only). The others also run on their own:
`Contoso.CefHostConsumer <scenario> [report.json]`.

On Windows, keep the state folder short (for example `%TEMP%\cefhost-state`): Chromium cannot create its cache in deep
paths.

| Scenario | What it covers |
|---|---|
| `main` | One Blazor window and one static window: rendering and .NET events, resource statuses and injected failures, bounds, window state, title and icon, acknowledged messages, refused popups and navigation, fragment and full navigation, a replaced page's late work, reloads, cross-window isolation, dispatch, and cleanup |
| `late-work-cef` | Only a replaced page's late work (render, JS interop, navigation), through the provider |
| `late-work-default` | The same pages on InfiniFrame's default Windows backend (WebView2) |
| `eval-contract` | Raw `ExecuteJavaScriptAsync` results for several value types, through the provider |
| `eval-contract-default` | The same evaluations on the default Windows backend |
| `renderer-loss` | The main window's renderer is killed: a window-level fault, reported and closed once |
| `companion-loss` | The companion is killed: the host fault is rethrown from `Run()`, and leftovers are removed |
| `send-failure` | A message for the active document that cannot be delivered is observable |
| `close-during-creation` | `Close()` from a `WindowCreating` handler, before the native window exists |
| `shutdown-during-creation` | `Shutdown()` while windows are still being created |
| `rejected-close` | A `Closing` handler is refused (no close cancellation); duplicate closes complete once |
| `native-close` | The native window is closed like a user would; the application observes it |
| `idle` | A connection idle for 40 s (beyond the 30 s partial-frame deadline) keeps working |
| `refusals` | Requests the provider cannot honour fail clearly, before any native window exists |

## Contoso.CoreGapRepro

A browser-free check of document ownership using an in-process fake provider that implements the public host contract. Run the sample with `gap`, `control` or `crosstalk`.

## LocalControlsHost

A native Blazor Hybrid application using the [LocalControlsUi Razor class library](LocalControlsUi/README.md). See [host setup and runtime checks](LocalControlsHost/README.md).
