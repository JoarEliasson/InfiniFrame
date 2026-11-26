using Contoso.InfiniFrame.CefHost;
using InfiniFrame;
using InfiniFrame.Application;
using InfiniFrame.Hosting;

namespace Contoso.CefHostConsumer;

/// <summary>Controlled faults with the real companion. Process kills are performed by the scenario driver, which
/// targets only the exact process ids this run reports (never processes by name).</summary>
internal static class FaultScenarios {
    /// <summary>The renderer of the main window is terminated: a window-level fault, reported and closed once.</summary>
    internal static async Task RendererLossAsync(Checks checks) {
        int closed = 0;
        ConsumerHost.Built built = ConsumerHost.Build(main: window => window.RegisterWindowClosedHandler(_ => Interlocked.Increment(ref closed)));
        (InfiniFrameApplication app, var provider, ProbeState state) = built;
        Task run = Task.Run(app.Run);
        IInfiniFrameWindow main = await ConsumerHost.WindowAsync(app, "main");
        await main.Features.Lifecycle.WaitForReadyAsync();
        checks.That("rendered before the fault", await Page.WaitFor(main, "document.querySelector('#assets')?.textContent === 'PASS'", 60000));
        int before = state.NativeEvents;
        Checks.Marker($"KILL-RENDERER companion={provider.GetDiagnostics()!.CompanionProcessId}");
        bool windowEnded = await Page.Until(() => main.LifecycleState >= InfiniFrameWindowLifecycleState.NativeClosed, 30000);
        checks.That("renderer loss ends the window (no silent continuation)", windowEnded, main.LifecycleState.ToString());
        Exception? runFault = null;
        try { await run.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (Exception exception) { runFault = exception; }
        CefHostDiagnosticsView diagnostics = new(provider);
        checks.Observe("provider-diagnostics", provider.GetDiagnostics());
        checks.That("the provider reported a window-level fault with the renderer reason",
            diagnostics.ViewFaults.Any(static reason => reason.Contains("renderer process terminated", StringComparison.Ordinal)), string.Join(" | ", diagnostics.ViewFaults));
        checks.That("WindowClosed ran exactly once", Volatile.Read(ref closed) == 1, closed);
        checks.That("the run ended (window-level fault, not host-fatal)", runFault is null, runFault?.Message);
        checks.That("no .NET interaction after the renderer loss", state.NativeEvents == before);
        checks.That("companion still shut down cleanly (exit 0, no escalation)", diagnostics.ExitCode == 0 && !diagnostics.Escalated,
            $"exit {diagnostics.ExitCode}, escalated {diagnostics.Escalated}, cleanup {diagnostics.Cleanup}");
        ConsumerHost.CheckRunCleanup(checks, provider.GetDiagnostics());
    }

    /// <summary>The companion process is terminated: host-fatal, rethrown from Run(), leftovers removed and reported.</summary>
    internal static async Task CompanionLossAsync(Checks checks) {
        int closed = 0;
        ConsumerHost.Built built = ConsumerHost.Build(main: window => window.RegisterWindowClosedHandler(_ => Interlocked.Increment(ref closed)));
        (InfiniFrameApplication app, var provider, _) = built;
        Task run = Task.Run(app.Run);
        IInfiniFrameWindow main = await ConsumerHost.WindowAsync(app, "main");
        await main.Features.Lifecycle.WaitForReadyAsync();
        checks.That("rendered before the fault", await Page.WaitFor(main, "document.querySelector('#assets')?.textContent === 'PASS'", 60000));
        Checks.Marker($"KILL-COMPANION companion={provider.GetDiagnostics()!.CompanionProcessId}");
        Exception? runFault = null;
        try { await run.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (Exception exception) { runFault = exception; }
        var diagnostics = new CefHostDiagnosticsView(provider);
        checks.Observe("provider-diagnostics", provider.GetDiagnostics());
        checks.That("companion loss faults the host and Run() rethrows it", runFault is InfiniFrameHostOperationException,
            runFault is null ? "no exception" : $"{runFault.GetType().Name}: {runFault.Message}");
        checks.That("open window ended once (WindowClosed exactly once)", Volatile.Read(ref closed) == 1, closed);
        checks.That("session fault recorded with the companion's exit", diagnostics.SessionFaults.Count > 0, string.Join(" | ", diagnostics.SessionFaults));
        ConsumerHost.CheckRunCleanup(checks, provider.GetDiagnostics(), companionKilled: true);
    }

    /// <summary>A message for the active document that cannot be delivered is observable (synchronous API throws).</summary>
    internal static async Task ActiveDocumentSendFailureAsync(Checks checks) {
        int closed = 0;
        ConsumerHost.Built built = ConsumerHost.Build(blazor: false, main: window => window.RegisterWindowClosedHandler(_ => Interlocked.Increment(ref closed)));
        (InfiniFrameApplication app, var provider, _) = built;
        Task run = Task.Run(app.Run);
        IInfiniFrameWindow main = await ConsumerHost.WindowAsync(app, "main");
        await main.Features.Lifecycle.WaitForReadyAsync();
        checks.That("static page loaded", await Page.WaitFor(main, ConsumerHost.StaticPageRendered, 60000));
        Exception? small = await Task.Run(() => Capture(() => main.SendWebMessage("small message to the active document")));
        checks.That("a normal synchronous send to the active document succeeds", small is null, small?.Message);
        string oversized = new('x', 9 * 1024 * 1024);
        Exception? failure = await Task.Run(() => Capture(() => main.SendWebMessage(oversized)));
        checks.That("an undeliverable active-document message throws from the synchronous API (not dropped)",
            failure is InvalidOperationException, failure is null ? "no exception" : $"{failure.GetType().FullName}: {failure.Message} <- {failure.InnerException?.Message}");
        bool ended = await Page.Until(() => main.LifecycleState >= InfiniFrameWindowLifecycleState.NativeClosed, 30000);
        checks.That("InfiniFrame faulted the window after the delivery failure (observable, closed once)", ended && Volatile.Read(ref closed) == 1,
            $"{main.LifecycleState}, closed {closed}");
        try { await run.WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (Exception exception) { checks.That("run ended", false, exception.Message); }
        CefHostDiagnostics? diagnostics = provider.GetDiagnostics();
        checks.Observe("provider-diagnostics", diagnostics);
        checks.That("the provider counted the failed active-document send",
            diagnostics?.MessageTypes.Any(static pair => pair.Key.StartsWith("out-failed", StringComparison.Ordinal)) == true,
            string.Join(",", diagnostics?.MessageTypes.Keys ?? []));
        ConsumerHost.CheckRunCleanup(checks, diagnostics);
    }

    private static Exception? Capture(Action action) {
        try {
            action();
            return null;
        }
        catch (Exception exception) {
            return exception;
        }
    }
}

/// <summary>Convenience view over the provider's public diagnostics.</summary>
internal sealed class CefHostDiagnosticsView(Contoso.InfiniFrame.CefHost.CefHostProvider provider) {
    private readonly Contoso.InfiniFrame.CefHost.CefHostDiagnostics _snapshot = provider.GetDiagnostics()!;
    internal IReadOnlyList<string> ViewFaults => _snapshot.ViewFaults;
    internal IReadOnlyList<string> SessionFaults => _snapshot.SessionFaults;
    internal int? ExitCode => _snapshot.CompanionExitCode;
    internal bool Escalated => _snapshot.ExitEscalated;
    internal string Cleanup => _snapshot.Cleanup;
}
