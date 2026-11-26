using Contoso.InfiniFrame.CefHost;
using InfiniFrame;
using InfiniFrame.Application;
using InfiniFrame.Security;

namespace Contoso.CefHostConsumer;

/// <summary>Close/creation races, native close, idle survival and capability refusals with the real companion.</summary>
internal static class LifecycleScenarios {
    /// <summary>Close() from a WindowCreating handler: the close is committed before the native window exists.</summary>
    internal static async Task CloseDuringCreationAsync(Checks checks) {
        int closed = 0;
        ConsumerHost.Built built = ConsumerHost.Build(blazor: false, main: window => window
            .RegisterWindowCreatingHandler(static creating => creating.Close())
            .RegisterWindowClosedHandler(_ => Interlocked.Increment(ref closed)));
        Task run = Task.Run(built.Application.Run);
        Exception? fault = await Capture(run);
        CefHostDiagnostics diagnostics = built.Provider.GetDiagnostics()!;
        checks.Observe("provider-diagnostics", diagnostics);
        checks.That("Run() returns after a close requested during creation", fault is null, fault?.Message);
        checks.That("the native window was created, then closed (request != completion: closure reported by the companion)",
            diagnostics.ViewsCreated == 1 && diagnostics.ViewsClosed == 1, $"created {diagnostics.ViewsCreated}, closed {diagnostics.ViewsClosed}");
        checks.That("WindowClosed ran once", Volatile.Read(ref closed) == 1, closed);
        Clean(checks, diagnostics);
    }

    /// <summary>Shutdown() while windows are still being created: a window created after shutdown began is reclaimed.</summary>
    internal static async Task ShutdownDuringCreationAsync(Checks checks) {
        InfiniFrameApplication? application = null;
        int closedMain = 0, closedLate = 0;
        ConsumerHost.Built built = ConsumerHost.Build(blazor: false, main: window => window
            .RegisterWindowCreatingHandler(_ => application?.Shutdown())
            .RegisterWindowClosedHandler(_ => Interlocked.Increment(ref closedMain)),
            extra: builder => builder.WithWindow("late", window => {
                ConsumerHost.Secure(window, "Window created after shutdown began", ConsumerHost.Origin + "secondary.html");
                window.SetLocation(200, 200).SetSize(400, 300);
                window.RegisterCustomSchemeHandler("app", StaticAssets.Handle);
                window.RegisterWindowClosedHandler(_ => Interlocked.Increment(ref closedLate));
            }));
        application = built.Application;
        Task run = Task.Run(application.Run);
        Exception? fault = await Capture(run);
        CefHostDiagnostics diagnostics = built.Provider.GetDiagnostics()!;
        checks.Observe("provider-diagnostics", diagnostics);
        checks.Observe("windows", new {
            createCommandsSent = diagnostics.CommandsSent.GetValueOrDefault("create"), diagnostics.ViewsCreated, diagnostics.ViewsClosed,
            diagnostics.ViewsReclaimed, closedMain, closedLate
        });
        checks.That("Run() returns after Shutdown() during creation", fault is null, fault?.Message);
        checks.That("every native window that was created was also closed (nothing abandoned)",
            diagnostics.ViewsCreated == diagnostics.ViewsClosed && diagnostics.ViewsCreated >= 1, $"created {diagnostics.ViewsCreated}, closed {diagnostics.ViewsClosed}");
        checks.That("WindowClosed runs exactly once for each window, created natively or not",
            Volatile.Read(ref closedMain) == 1 && Volatile.Read(ref closedLate) == 1,
            $"main {closedMain}, late {closedLate}; native creates requested {diagnostics.CommandsSent.GetValueOrDefault("create")}, created {diagnostics.ViewsCreated}");
        Clean(checks, diagnostics);
    }

    /// <summary>
    ///     This provider has no native close-request event, so it does not declare CloseCancellation: a Closing handler
    ///     is refused before any window exists. Duplicate close calls complete once.
    /// </summary>
    internal static async Task RejectedAndDuplicateCloseAsync(Checks checks) {
        ConsumerHost.Built refused = ConsumerHost.Build(blazor: false, main: window =>
            window.RegisterWindowClosingHandler((_, _) => WindowClosingResult.Cancel));
        Exception? refusal = await Capture(Task.Run(refused.Application.Run));
        CefHostDiagnostics refusedDiagnostics = refused.Provider.GetDiagnostics()!;
        checks.That("a Closing (cancellable close) handler is refused clearly: the provider does not declare CloseCancellation",
            refusal is NotSupportedException && refusal.Message.Contains("CloseCancellation", StringComparison.Ordinal),
            refusal is null ? "no exception" : $"{refusal.GetType().Name}: {refusal.Message}");
        checks.That("the refused window never reached the companion", refusedDiagnostics.ViewsCreated == 0, refusedDiagnostics.ViewsCreated);

        int closed = 0;
        ConsumerHost.Built built = ConsumerHost.Build(blazor: false, main: window => window.RegisterWindowClosedHandler(_ => Interlocked.Increment(ref closed)));
        Task run = Task.Run(built.Application.Run);
        IInfiniFrameWindow main = await ConsumerHost.WindowAsync(built.Application, "main");
        await main.Features.Lifecycle.WaitForReadyAsync();
        await Page.WaitFor(main, ConsumerHost.StaticPageRendered, 60000);
        Task first = main.CloseAsync().AsTask();
        Task second = main.CloseAsync().AsTask();
        main.Close();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        Exception? fault = await Capture(run);
        CefHostDiagnostics diagnostics = built.Provider.GetDiagnostics()!;
        checks.Observe("provider-diagnostics", diagnostics);
        checks.That("three overlapping close calls complete; WindowClosed runs once", fault is null && Volatile.Read(ref closed) == 1,
            $"closed {closed}, close commands sent {diagnostics.CommandsSent.GetValueOrDefault("close")}");
        Clean(checks, diagnostics);
    }

    /// <summary>The driver closes the native window like a user (WM_CLOSE on Windows); the application observes closure.</summary>
    internal static async Task NativeCloseAsync(Checks checks) {
        int closed = 0;
        string title = $"Native close probe {Guid.NewGuid():N}";
        ConsumerHost.Built built = ConsumerHost.Build(blazor: false, main: window => {
            window.SetTitle(title);
            window.RegisterWindowClosedHandler(_ => Interlocked.Increment(ref closed));
        });
        Task run = Task.Run(built.Application.Run);
        IInfiniFrameWindow main = await ConsumerHost.WindowAsync(built.Application, "main");
        await main.Features.Lifecycle.WaitForReadyAsync();
        await Page.WaitFor(main, ConsumerHost.StaticPageRendered, 60000);
        Checks.Marker($"NATIVE-CLOSE title={title}");
        Exception? fault = await Capture(run);
        CefHostDiagnostics diagnostics = built.Provider.GetDiagnostics()!;
        checks.Observe("provider-diagnostics", diagnostics);
        checks.That("a native (user) close ends the window and the run", fault is null && Volatile.Read(ref closed) == 1, fault?.Message);
        checks.That("the application never asked for that close (no close command sent)", diagnostics.CommandsSent.GetValueOrDefault("close") == 0,
            diagnostics.CommandsSent.GetValueOrDefault("close"));
        Clean(checks, diagnostics);
    }

    /// <summary>An idle live connection (no frames for 40 s, beyond the 30 s partial-frame deadline) keeps working.</summary>
    internal static async Task IdleAsync(Checks checks) {
        ConsumerHost.Built built = ConsumerHost.Build(blazor: false);
        Task run = Task.Run(built.Application.Run);
        IInfiniFrameWindow main = await ConsumerHost.WindowAsync(built.Application, "main");
        await main.Features.Lifecycle.WaitForReadyAsync();
        await Page.WaitFor(main, ConsumerHost.StaticPageRendered, 60000);
        const string probe = "JSON.stringify({title: document.title, ready: document.readyState, visibility: document.visibilityState, marker: 'idle-probe'})";
        string? rawBefore = await main.Features.JavaScript.ExecuteJavaScriptAsync(probe);
        checks.Observe("probe-before-idle-raw", rawBefore);
        CefHostDiagnostics before = built.Provider.GetDiagnostics()!;
        await Task.Delay(TimeSpan.FromSeconds(40));
        CefHostDiagnostics after = built.Provider.GetDiagnostics()!;
        checks.That("no frames were exchanged during the idle period",
            before.FramesReceived.Values.Sum() == after.FramesReceived.Values.Sum() && before.CommandsSent.Values.Sum() == after.CommandsSent.Values.Sum(),
            $"received {before.FramesReceived.Values.Sum()} -> {after.FramesReceived.Values.Sum()}");
        var answerClock = System.Diagnostics.Stopwatch.StartNew();
        string? rawAfter = await main.Features.JavaScript.ExecuteJavaScriptAsync(probe);
        checks.Observe("probe-after-idle-raw", rawAfter);
        checks.Observe("probe-after-idle-ms", answerClock.ElapsedMilliseconds);
        CefHostDiagnostics answered = built.Provider.GetDiagnostics()!;
        checks.That("after 40 s idle the session still answers (idle wait is unbounded; partial-frame deadline is 30 s)",
            rawAfter is not null && rawAfter.Contains("idle-probe", StringComparison.Ordinal) && rawAfter.Contains("Isolated secondary view", StringComparison.Ordinal)
            && answered.SessionFaults.Count == 0,
            $"{rawAfter ?? "(null)"} in {answerClock.ElapsedMilliseconds} ms; before idle {rawBefore ?? "(null)"}");
        await main.CloseAsync();
        Exception? fault = await Capture(run);
        CefHostDiagnostics diagnostics = built.Provider.GetDiagnostics()!;
        checks.Observe("provider-diagnostics", diagnostics);
        checks.That("run ended normally", fault is null, fault?.Message);
        Clean(checks, diagnostics);
    }

    /// <summary>Explicit requests this provider cannot honour fail clearly, before any native window exists.</summary>
    internal static async Task RefusalsAsync(Checks checks) {
        (string Name, Action<IInfiniFrameWindowBuilder> Configure, string Expect)[] cases = [
            ("permissive builder defaults", static window => {
                window.SetTitle("defaults").SetLocation(100, 100).SetSize(800, 600).SetStartPageUrl(ConsumerHost.Origin + "secondary.html");
                window.AddTrustedOrigin(ConsumerHost.Origin);
                window.RegisterCustomSchemeHandler("app", StaticAssets.Handle);
            }, "context menu"),
            ("additional custom scheme", static window => {
                ConsumerHost.Secure(window, "scheme", ConsumerHost.Origin + "secondary.html");
                window.RegisterCustomSchemeHandler("app", StaticAssets.Handle);
                window.RegisterCustomSchemeHandler("probe", StaticAssets.Handle);
            }, "custom scheme 'probe'"),
            ("additional trusted origin", static window => {
                ConsumerHost.Secure(window, "origin", ConsumerHost.Origin + "secondary.html");
                window.AddTrustedOrigin("https://example.com/");
                window.RegisterCustomSchemeHandler("app", StaticAssets.Handle);
            }, "trusted origin https://example.com/"),
            ("bounds notifications (undeclared capability)", static window => {
                ConsumerHost.Secure(window, "events", ConsumerHost.Origin + "secondary.html");
                window.RegisterCustomSchemeHandler("app", StaticAssets.Handle);
                window.RegisterLocationChangedHandler((_, _) => {});
            }, "BoundsEvents")
        ];
        foreach ((string name, Action<IInfiniFrameWindowBuilder> configure, string expect) in cases) {
            InfiniFrameApplicationBuilder builder = InfiniFrameApplication.CreateBuilder();
            var provider = new CefHostProvider(ConsumerHost.Options());
            builder.UseHostProvider(provider);
            builder.WithWindow("main", configure);
            Exception? fault;
            try {
                using InfiniFrameApplication application = builder.Build();
                fault = await Capture(Task.Run(application.Run));
            }
            catch (Exception exception) { fault = exception; }
            CefHostDiagnostics? diagnostics = provider.GetDiagnostics();
            checks.That($"refused clearly: {name}", fault is NotSupportedException && fault.Message.Contains(expect, StringComparison.Ordinal)
                && (diagnostics?.ViewsCreated ?? 0) == 0,
                fault is null ? "no exception" : $"{fault.GetType().Name}: {Checks.Shorten(fault.Message)}");
        }
    }

    private static async Task<Exception?> Capture(Task task) {
        try {
            await task.WaitAsync(TimeSpan.FromSeconds(90));
            return null;
        }
        catch (Exception exception) {
            return exception;
        }
    }

    private static void Clean(Checks checks, CefHostDiagnostics diagnostics) {
        checks.That("companion exit 0, no escalation", diagnostics.CompanionExitCode == 0 && !diagnostics.ExitEscalated,
            $"exit {diagnostics.CompanionExitCode}, escalated {diagnostics.ExitEscalated}");
        ConsumerHost.CheckRunCleanup(checks, diagnostics);
    }
}
