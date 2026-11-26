using Contoso.InfiniFrame.CefHost.Tests.Infrastructure;
using InfiniFrame.Hosting;

namespace Contoso.InfiniFrame.CefHost.Tests;

/// <summary>6. The companion dies (exit code 3) right after hello, printing a diagnostic to stderr first.</summary>
[Category("Fake")]
public sealed class CrashExitTests {
    private const string Diagnostic = "fake companion fatal error (simulated crash after hello)";

    [Test]
    [Timeout(60_000)]
    public async Task CompanionCrash_IsReportedWithItsExitCodeAndLastDiagnostics(CancellationToken cancellationToken) {
        await using HostHarness harness = HostHarness.Create("crash", "crash-after-hello", ["--exit-code", "3"]);
        await harness.StartAsync();

        Exception? completion = await Wait.ExceptionAsync(() => harness.Host.Completion, "host.Completion to fault");
        await harness.ShutdownAsync();
        TimeSpan disposal = await harness.DisposeHostAsync();
        // After disposal every session loop (including the exit observer) has finished: the record is final.
        CefHostDiagnostics final = harness.Diagnostics;
        IReadOnlyList<ProcessIdentity> leftovers = await harness.LeftoverProcessesAsync();
        string faults = string.Join(" | ", final.SessionFaults);

        using (Assert.Multiple()) {
            await Assert.That(final.CompanionExitCode).IsEqualTo(3);
            await Assert.That(final.SessionFaults.Count).IsGreaterThan(0);
            await Assert.That(final.SessionFaults.Any(static fault => fault.Contains("exited with code 3", StringComparison.Ordinal))).IsTrue()
                .Because($"the session fault should name the exit code; session faults: [{faults}]");
            await Assert.That(final.SessionFaults.Any(static fault => fault.Contains(Diagnostic, StringComparison.Ordinal))).IsTrue()
                .Because($"the session fault should carry the companion's last diagnostic; session faults: [{faults}]");
            await Assert.That(completion is InfiniFrameHostOperationException).IsTrue().Because($"Completion: {completion?.GetType().Name}: {completion?.Message}");
            await Assert.That(completion?.Message ?? "").Contains("code 3");
            await Assert.That(final.CompanionStderrTail.Any(static line => line.Contains(Diagnostic, StringComparison.Ordinal))).IsTrue();
            await Assert.That(final.ExitEscalated).IsFalse();
            await Assert.That(disposal).IsLessThan(harness.Options.ExitTimeout + TimeSpan.FromSeconds(3));
            await Assert.That(leftovers.Count).IsEqualTo(0);
        }
    }
}
