using System.Diagnostics;
using System.Text.Json;
using InfiniFrame;
using InfiniFrame.Application;

namespace Contoso.CefHostConsumer;

/// <summary>The main real-browser scenario: one Blazor window plus one static window through the external CEF provider.</summary>
internal static class MainScenario {
    internal static async Task RunAsync(Checks checks) {
        var clock = Stopwatch.StartNew();
        int mainHandlerThread = 0;
        int callbackNavigations = 0;
        ProbeState? stateRef = null;
        ConsumerHost.Built built = ConsumerHost.Build(secondary: true, main: window => {
            window.RegisterWebMessageReceivedHandler((sender, message) => {
                Volatile.Write(ref mainHandlerThread, Environment.CurrentManagedThreadId);
                if (SynchronizationContext.Current is not null && stateRef is not null) Interlocked.Increment(ref stateRef.HandlerSawSynchronizationContext);
                if (message == "__bwv:secondary-isolation" && stateRef is not null) Interlocked.Increment(ref stateRef.CrossWindowMessages);
                if (message == "__bwv:callback-navigation" && Interlocked.Increment(ref callbackNavigations) == 1) {
                    // Synchronous navigation from inside a message callback (on the dispatcher): must not wait on itself.
                    sender.Features.PageNavigation.Load(new Uri("app://localhost/?callback-navigation=1"));
                    if (stateRef is not null) Interlocked.Increment(ref stateRef.CallbackNavigationsCompleted);
                }
            });
        });
        (InfiniFrameApplication app, var provider, ProbeState state) = built;
        stateRef = state;
        checks.Observe("build-ms", clock.ElapsedMilliseconds);
        Task run = Task.Run(app.Run);
        try {
            IInfiniFrameWindow main = await ConsumerHost.WindowAsync(app, "main");
            IInfiniFrameWindow secondary = await ConsumerHost.WindowAsync(app, "secondary");
            await main.Features.Lifecycle.WaitForReadyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
            checks.Observe("ready-ms", clock.ElapsedMilliseconds);
            bool rendered = await Page.WaitFor(main, "document.querySelector('#assets')?.textContent === 'PASS'", 60000);
            checks.That("first render: module, binary, scoped CSS and package assets PASS", rendered, $"{clock.ElapsedMilliseconds} ms after build");
            checks.Observe("first-render-ms", clock.ElapsedMilliseconds);
            Checks.Marker("READY");

            // ---- Native WebView hosting, not Blazor Server ------------------------------------------------------------
            JsonElement boot = await Page.EvaluateJson(main, """
                (()=>({ href: location.href, blazor: typeof window.Blazor, bridge: typeof window.infiniframe?.host?.postData,
                  scripts: [...document.scripts].map(s=>s.src),
                  errorUi: getComputedStyle(document.querySelector('#blazor-error-ui')).display,
                  h1Color: getComputedStyle(document.querySelector('h1')).color, marker: window.__documentMarker }))()
                """);
            string[] scripts = [.. boot.GetProperty("scripts").EnumerateArray().Select(static e => e.GetString()!)];
            checks.That("bootstrap is blazor.webview.js from app://localhost (native WebView), no Blazor Server script",
                scripts.Contains("app://localhost/_framework/blazor.webview.js")
                && !scripts.Any(static r => r.Contains("blazor.server", StringComparison.Ordinal) || r.Contains("blazor.web.js", StringComparison.Ordinal))
                && scripts.All(static r => r.StartsWith("app://localhost/", StringComparison.Ordinal)), string.Join(", ", scripts));
            checks.That("page origin and native bridge", boot.GetProperty("href").GetString() == "app://localhost/"
                && boot.GetProperty("bridge").GetString() == "function" && boot.GetProperty("blazor").GetString() == "object", boot.ToString());
            checks.That("stylesheets applied (app.css hides error UI; scoped CSS colours h1)",
                boot.GetProperty("errorUi").GetString() == "none" && boot.GetProperty("h1Color").GetString() == "rgb(39, 92, 145)", boot.ToString());
            IReadOnlyDictionary<string, int> paths = provider.GetDiagnostics()!.ResourcePaths;
            checks.That("host page and blazor.webview.js were served through the provider's resource relay (status 200)",
                paths.GetValueOrDefault("GET /_framework/blazor.webview.js") == 200 && paths.GetValueOrDefault("GET /") == 200,
                string.Join(", ", paths.Select(static pair => $"{pair.Key}={pair.Value}").Take(24)));
            string marker1 = boot.GetProperty("marker").GetString()!;
            checks.Observe("page-visibility-and-charset", await Page.EvaluateJson(main,
                "(()=>({visibility: document.visibilityState, focus: document.hasFocus(), charset: document.characterSet, type: document.contentType}))()"));

            // ---- UI event in .NET updates the DOM ----------------------------------------------------------------------
            int before = state.NativeEvents;
            var latency = Stopwatch.StartNew();
            await Page.Click(main, "#increment");
            bool clicked = await Page.WaitFor(main, $"Number(document.querySelector('#counter').textContent) === {before + 1}");
            checks.That("DOM click runs a .NET handler and re-renders the DOM", clicked && state.NativeEvents == before + 1,
                $"{latency.ElapsedMilliseconds} ms, handler threads {string.Join(",", state.HandlerThreads.Keys)}, window dispatcher {main.ManagedThreadId}");
            await Page.Evaluate(main, "(()=>{const i=document.querySelector('#probe-input');i.value='native-input';i.dispatchEvent(new Event('change',{bubbles:true}));return true})()");
            checks.That("input binding round-trips through .NET", await Page.WaitFor(main, "document.querySelector('#input-value').textContent === 'native-input'"));

            // ---- .NET-originated updates (no page-side substitute) ------------------------------------------------------
            long ticks = state.Ticks;
            checks.That("component timer (.NET-originated) updates the DOM", await Page.WaitFor(main, $"Number(document.querySelector('#managed-ticks').textContent) > {ticks}"));
            checks.That("application-lifetime producer updates page 1", await Page.WaitFor(main, "Number(document.querySelector('#producer-ticks').textContent) > 0"),
                $"started by page {await Page.Evaluate(main, "document.querySelector('#producer-origin').textContent")}");

            // ---- Resources through the real browser path ----------------------------------------------------------------
            JsonElement statuses = await Page.EvaluateAsyncJson(main, """
                window.probe.resourceStatuses([["GET","app.css"],["HEAD","app.css"],["GET","missing-required-asset.js"],
                  ["GET","__fault/throws.css"],["GET","__fault/read-error.bin"],["GET","__fault/oversize.bin"],
                  ["GET","__fault/oversize-stream.bin"],["GET","probe.ico"]])
                """);
            checks.Observe("resource-statuses", statuses);
            int Status(string method, string path) => statuses.EnumerateArray()
                .Where(e => e.GetProperty("method").GetString() == method && e.GetProperty("path").GetString() == path)
                .Select(static e => e.TryGetProperty("status", out JsonElement s) ? s.GetInt32() : -1).FirstOrDefault(-1);
            checks.That("200 for a present asset", Status("GET", "app.css") == 200);
            checks.That("HEAD 200 with no body", Status("HEAD", "app.css") == 200);
            checks.That("404 for a missing asset (not the host page)", Status("GET", "missing-required-asset.js") == 404);
            checks.That("500 when the asset store throws (InfiniFrame's status, relayed unchanged)", Status("GET", "__fault/throws.css") == 500);
            checks.That("502 when the body fails mid-read (provider relay error, request only)", Status("GET", "__fault/read-error.bin") == 502);
            checks.That("502 for a 9 MiB seekable body over the 8 MiB frame budget", Status("GET", "__fault/oversize.bin") == 502);
            checks.That("502 for a 9 MiB non-seekable body (bounded read)", Status("GET", "__fault/oversize-stream.bin") == 502);
            checks.That("binary asset still 200 after the failures", Status("GET", "probe.ico") == 200);
            JsonElement css = statuses.EnumerateArray().First(static e => e.GetProperty("path").GetString() == "app.css");
            checks.That("responses carry nosniff, no-store and a CSP", css.GetProperty("nosniff").GetString() == "nosniff"
                && css.GetProperty("cache").GetString() == "no-store" && css.GetProperty("csp").GetString()?.Contains("default-src 'self'") == true,
                css.GetProperty("csp").GetString());
            before = state.NativeEvents;
            await Page.Click(main, "#increment");
            checks.That("session healthy after request failures (interaction still works)",
                await Page.WaitFor(main, $"Number(document.querySelector('#counter').textContent) === {before + 1}"));
            JsonElement inline = await Page.EvaluateJson(main, "window.probe.inlineStyleProbe()");
            checks.Observe("csp-inline-behaviour", inline);
            checks.That("CSP recorded: inline styles fall under default-src 'self' (style-src not set)", true, inline.ToString());

            // ---- Native window operations ------------------------------------------------------------------------------
            await NativeWindowAsync(checks, main);

            // ---- Navigation: fragment, history, full navigation ----------------------------------------------------------
            string probeId = Guid.NewGuid().ToString("N");
            await Page.Evaluate(main, $"(()=>{{window.__fragmentProbe='{probeId}';return true}})()");
            NavigationResult fragment = await main.Features.PageNavigation.LoadAsync(new Uri("app://localhost/#fragment-probe"));
            bool fragmentKept = await Page.WaitFor(main, $"location.hash === '#fragment-probe' && window.__fragmentProbe === '{probeId}'");
            checks.That("fragment navigation keeps its document (context preserved)", fragment.Status == NavigationStatus.Succeeded && fragmentKept,
                $"{fragment.Status}, current {main.Features.PageNavigation.GetCurrentUrl()}");
            await Page.Evaluate(main, "(()=>{history.replaceState(null,'',new URL('#history-probe',location.href).href);return true})()");
            checks.That("actual main-frame URL follows same-document history",
                await Page.Until(() => main.Features.PageNavigation.GetCurrentUrl() == "app://localhost/#history-probe", 5000),
                main.Features.PageNavigation.GetCurrentUrl());

            // ---- Document ownership: stale page-1 work, and the page-1 producer driving page 2 -------------------------
            await Page.Click(main, "#delayed-stamp");// page 1 schedules a delayed render + JS call (1.5 s)
            var reloadClock = Stopwatch.StartNew();
            NavigationResult reload = await main.Features.PageNavigation.LoadAsync(new Uri("app://localhost/screen?page=2"));
            bool page2 = await Page.WaitFor(main, "document.querySelector('#assets')?.textContent === 'PASS'");
            string marker2 = await Page.Evaluate(main, "window.__documentMarker");
            checks.That("full navigation creates a new document", reload.Status == NavigationStatus.Succeeded && page2 && marker2 != marker1,
                $"{reload.Status} in {reloadClock.ElapsedMilliseconds} ms; markers {marker1[..8]} -> {marker2[..8]}");
            checks.Observe("reload-ms", reloadClock.ElapsedMilliseconds);
            await Task.Delay(state.DelayedMilliseconds + 1500);
            JsonElement afterLate = await Page.EvaluateJson(main,
                "(()=>({stamps: window.__probeStamps, lastStamp: document.documentElement.dataset.lastStamp ?? null, late: document.querySelector('#late-stamp').textContent, page: document.querySelector('#page-instance').textContent, marker: window.__documentMarker, location: location.href}))()");
            string? browserUrl = main.Features.PageNavigation.GetCurrentUrl();
            // Wait (bounded) for the outcome of page 1's late call on Blazor's dispatcher (refused, or its 2 s token).
            await Page.Until(() => state.DelayedOutcomes.Any(static outcome => outcome.StartsWith("js on dispatcher", StringComparison.Ordinal)), 5000);
            checks.Observe("late-work-page2-after-delay", afterLate);
            checks.Observe("late-work-browser-current-url-after-delay", browserUrl);
            checks.Observe("late-work-delayed-outcomes", state.DelayedOutcomes.ToArray());
            bool attempted = state.DelayedRenderAttempts == 1 && state.DelayedJsAttempts == 1;
            bool notRendered = afterLate.GetProperty("late").GetString() == "none";
            bool noJs = afterLate.GetProperty("stamps").GetArrayLength() == 0 && afterLate.GetProperty("lastStamp").ValueKind == JsonValueKind.Null;
            checks.That("setup: page 1's delayed render and JS call both ran after page 1 was replaced", attempted,
                string.Join("; ", state.DelayedOutcomes));
            checks.That("page 1's late render did not reach page 2's DOM", notRendered, afterLate.ToString());
            checks.That("page 1's late JS interop call was not executed by page 2", noJs, afterLate.ToString());
            checks.That("setup: page 1's late navigation and late JS call on Blazor's dispatcher both ran after page 1 was replaced",
                state.DelayedNavigationAttempts == 1 && state.DelayedDispatcherJsAttempts == 1, string.Join("; ", state.DelayedOutcomes));
            checks.That("page 1's late navigation did not move page 2's document (DOM location and browser URL unchanged)",
                afterLate.GetProperty("location").GetString() == "app://localhost/screen?page=2" && afterLate.GetProperty("marker").GetString() == marker2
                && browserUrl == "app://localhost/screen?page=2", $"{afterLate}; browser {browserUrl}");
            checks.That("page 1's late JS call on Blazor's dispatcher was refused with JSDisconnectedException (not run, not answered by page 2)",
                state.DelayedOutcomes.Contains("js on dispatcher page 1: JSDisconnectedException"), string.Join("; ", state.DelayedOutcomes));
            long producer2 = long.Parse(await Page.Evaluate(main, "document.querySelector('#producer-ticks').textContent"), System.Globalization.CultureInfo.InvariantCulture);
            bool producerAdvanced = await Page.WaitFor(main, $"Number(document.querySelector('#producer-ticks').textContent) > {producer2 + 2} && window.__documentMarker === '{marker2}'");
            checks.That("the producer started under page 1 keeps updating page 2's DOM", producerAdvanced && afterLate.GetProperty("page").GetString() == "2",
                $"producer started by page {await Page.Evaluate(main, "document.querySelector('#producer-origin').textContent")}");
            checks.That("application service identity stable across documents; one live page component",
                await Page.Evaluate(main, "document.querySelector('#identity').textContent") == state.Identity.ToString()
                && state.ComponentsOpened - state.ComponentsDisposed == 1, $"opened {state.ComponentsOpened}, disposed {state.ComponentsDisposed}");

            // ---- Repeated reloads (resource observation) ---------------------------------------------------------------
            var reloadTimes = new List<long>();
            for (int iteration = 0; iteration < 5; iteration++) {
                var one = Stopwatch.StartNew();
                if (iteration % 2 == 0) await main.Features.PageNavigation.LoadAsync(new Uri($"app://localhost/screen?iteration={iteration}"));
                else await Page.Click(main, "#reload");// NavigationManager forceLoad from the page
                bool ok = await Page.WaitFor(main, $"document.querySelector('#assets')?.textContent === 'PASS' && Number(document.querySelector('#page-instance').textContent) >= {3 + iteration}");
                reloadTimes.Add(one.ElapsedMilliseconds);
                if (!ok) checks.That($"reload {iteration} rendered", false);
            }
            checks.Observe("reload-loop-ms", reloadTimes);
            checks.That("five further reloads (navigation and page-initiated) keep one service and one live component",
                state.ComponentsOpened - state.ComponentsDisposed == 1 && await Page.Evaluate(main, "document.querySelector('#identity').textContent") == state.Identity.ToString(),
                $"opened {state.ComponentsOpened}, disposed {state.ComponentsDisposed}, times {string.Join(",", reloadTimes)}");
            Checks.Marker("RELOADS-DONE");

            // ---- Callback navigation from a message handler (no self-wait) --------------------------------------------
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5))) {
                try { await main.Features.JavaScript.ExecuteJavaScriptAsync("(()=>{window.infiniframe.host.postData('__bwv:callback-navigation');return true})()", timeout.Token); }
                catch (OperationCanceledException) {}
            }
            bool navigated = await Page.Until(() => main.Features.PageNavigation.GetCurrentUrl() == "app://localhost/?callback-navigation=1", 10000)
                && await Page.WaitFor(main, "document.querySelector('#assets')?.textContent === 'PASS'");
            checks.That("synchronous navigation from a message callback completes exactly once (no dispatcher self-wait)",
                navigated && Volatile.Read(ref callbackNavigations) == 1 && state.CallbackNavigationsCompleted == 1);

            // ---- Severing the owned bridge stops native interactivity; a fresh document recovers ----------------------
            before = state.NativeEvents;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3))) {
                try {
                    await main.Features.JavaScript.ExecuteJavaScriptAsync(
                        "(()=>{window.infiniframe.host.postData=()=>{throw new Error('test-only severed bridge')};document.querySelector('#increment').click();return true})()",
                        timeout.Token);
                    checks.That("severed bridge: the evaluation reply itself must not arrive", false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) {}
            }
            await Task.Delay(500);
            checks.That("severed bridge: the click did not reach .NET (no fallback path)", state.NativeEvents == before, $"{before} -> {state.NativeEvents}");
            await main.Features.PageNavigation.LoadAsync(new Uri("app://localhost/?recovery=1"));
            bool recovered = await Page.WaitFor(main, "document.querySelector('#assets')?.textContent === 'PASS'");
            await Page.Click(main, "#increment");
            checks.That("fresh document recovers native interactivity with the same application service",
                recovered && await Page.WaitFor(main, $"Number(document.querySelector('#counter').textContent) === {before + 1}")
                && await Page.Evaluate(main, "document.querySelector('#identity').textContent") == state.Identity.ToString());

            // ---- Window isolation and close of one window ---------------------------------------------------------------
            await secondary.Features.Lifecycle.WaitForReadyAsync();
            bool secondaryLoaded = await Page.WaitFor(secondary, ConsumerHost.StaticPageRendered);
            await Page.Evaluate(secondary, "(()=>{window.infiniframe.host.postData('__bwv:secondary-isolation');return true})()");
            await Page.Until(() => Volatile.Read(ref state.SecondaryMessages) > 0, 5000);
            checks.That("secondary window messages stay in their window", secondaryLoaded && state.SecondaryMessages == 1 && state.CrossWindowMessages == 0,
                $"secondary {state.SecondaryMessages}, crossed {state.CrossWindowMessages}");
            await secondary.CloseAsync();
            await secondary.Features.Lifecycle.WaitForClosedCallbacksAsync();
            before = state.NativeEvents;
            await Page.Click(main, "#increment");
            checks.That("closing one window leaves the other running; its WindowClosed ran once",
                await Page.WaitFor(main, $"Number(document.querySelector('#counter').textContent) === {before + 1}") && state.SecondaryClosed == 1,
                $"secondary lifecycle {secondary.LifecycleState}, closed callbacks {state.SecondaryClosed}");

            // ---- Threads ------------------------------------------------------------------------------------------------
            var diagnostics = provider.GetDiagnostics()!;
            checks.Observe("threads", new {
                dispatcher = main.ManagedThreadId, messageHandler = Volatile.Read(ref mainHandlerThread), clickHandlers = state.HandlerThreads.Keys.ToArray(),
                providerRoles = diagnostics.ThreadsByRole, handlerSawSynchronizationContext = state.HandlerSawSynchronizationContext
            });
            bool providerThreadsDistinct = diagnostics.ThreadsByRole.Values.SelectMany(static ids => ids).All(id => id != main.ManagedThreadId);
            checks.That("application message callbacks run on InfiniFrame's dispatcher, never on provider threads",
                Volatile.Read(ref mainHandlerThread) == main.ManagedThreadId && providerThreadsDistinct, JsonSerializer.Serialize(diagnostics.ThreadsByRole));
            ControlScenarios.ObserveModules(checks);
            Checks.Marker("BEFORE-CLOSE");

            // ---- Close and disposal -------------------------------------------------------------------------------------
            var close = Stopwatch.StartNew();
            await main.CloseAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(60));
            checks.Observe("close-to-run-return-ms", close.ElapsedMilliseconds);
            checks.That("Run() returns after the last window closes", run.IsCompletedSuccessfully);
        }
        finally {
            if (!run.IsCompleted) {
                app.Shutdown();
                try { await run.WaitAsync(TimeSpan.FromSeconds(60)); }
                catch (Exception exception) { checks.That("run ended after shutdown", false, exception.Message); }
            }
            var diagnostics = provider.GetDiagnostics();
            checks.Observe("provider-diagnostics", diagnostics);
            checks.That("companion exited 0 without escalation", diagnostics?.CompanionExitCode == 0 && diagnostics.ExitEscalated == false,
                $"exit {diagnostics?.CompanionExitCode}, escalated {diagnostics?.ExitEscalated}");
            ConsumerHost.CheckRunCleanup(checks, diagnostics);
            checks.That("no abandoned command left; no late faults", diagnostics?.PendingCommands == 0 && diagnostics.LateFaults.Count == 0,
                $"late faults [{string.Join(" | ", diagnostics?.LateFaults ?? [])}]; expected late retired rejections {diagnostics?.LateRetiredRejections}; late completions {diagnostics?.LateCompletions}");
        }
    }

    private static async Task NativeWindowAsync(Checks checks, IInfiniFrameWindow main) {
        try {
            main.Features.Size.SetSize(820, 640);
            main.Features.Position.SetLocation(120, 110);
            System.Drawing.Size size = main.Features.Size.Size;
            System.Drawing.Point location = main.Features.Position.Location;
            checks.That("bounds set and read back from the native window", size.Width == 820 && size.Height == 640 && location.X == 120 && location.Y == 110,
                $"{location.X},{location.Y} {size.Width}x{size.Height}");
        }
        catch (Exception exception) { checks.That("bounds set and read back from the native window", false, exception.Message); }
        try {
            main.Features.Decorations.SetTitle("InfiniFrame CEF λ provider");
            checks.That("Unicode title round-trips", main.Features.Decorations.Title == "InfiniFrame CEF λ provider", main.Features.Decorations.Title);
        }
        catch (Exception exception) { checks.That("Unicode title round-trips", false, exception.Message); }
        foreach ((string name, Action request, Func<bool> expected) in new (string, Action, Func<bool>)[] {
                     ("fullscreen-enter", () => main.Features.State.SetFullScreen(), () => main.Features.State.IsFullScreen),
                     ("fullscreen-exit", () => main.Features.State.SetFullScreen(false), () => !main.Features.State.IsFullScreen),
                     ("minimize", () => main.Features.State.SetMinimized(), () => main.Features.State.IsMinimized),
                     ("restore-after-minimize", () => main.Features.State.SetMinimized(false), () => !main.Features.State.IsMinimized),
                     ("maximize", () => main.Features.State.SetMaximized(), () => main.Features.State.IsMaximized),
                     ("restore-after-maximize", () => main.Features.State.SetMaximized(false), () => !main.Features.State.IsMaximized)
                 }) {
            try {
                request();
                checks.That($"native state {name} confirmed", await Page.Until(expected, 5000));
            }
            catch (Exception exception) { checks.That($"native state {name} confirmed", false, $"{exception.GetType().Name}: {exception.Message}"); }
        }
        try {
            main.Features.State.SetFocused();
            checks.That("focus request accepted (focus observation recorded)", true, $"focused={main.Features.State.IsFocused}");
            main.Features.Decorations.SetIconFile(Path.Combine(AppContext.BaseDirectory, "wwwroot", "probe.png"));
            checks.That("PNG icon applied natively", true);
        }
        catch (Exception exception) { checks.That("focus and icon", false, exception.Message); }
        try {
            // The bridge acknowledges InfiniFrame envelopes.
            const string acknowledgementProbe = """{"id":"__infiniframe:ready:ack","command":"Post","version":2}""";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await main.Features.WebMessaging.SendWebMessageWithAcknowledgementAsync(acknowledgementProbe, timeout.Token);
            checks.That("acknowledged message round-trips through the page", true);
        }
        catch (Exception exception) { checks.That("acknowledged message round-trips through the page", false, exception.Message); }
        checks.That("popup denied natively", await Page.Evaluate(main, "window.open('app://other/denied') === null") == "true");
        NavigationResult external = await main.Features.PageNavigation.LoadAsync(new Uri("https://example.com/"));
        checks.That("untrusted top-level navigation refused before reaching the browser", external.Status != NavigationStatus.Succeeded, external.Status.ToString());
        await Page.Evaluate(main, "(()=>{location.href='app://other/denied';return true})()");
        await Task.Delay(300);
        string location2 = await Page.Evaluate(main, "location.href");
        checks.That("page-initiated navigation to another origin blocked natively", location2.StartsWith("app://localhost/", StringComparison.Ordinal), location2);
    }
}
