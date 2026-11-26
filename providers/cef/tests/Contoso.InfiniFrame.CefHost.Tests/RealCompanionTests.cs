using System.Diagnostics;
using Contoso.InfiniFrame.CefHost.Tests.Infrastructure;
using InfiniFrame.Hosting;

namespace Contoso.InfiniFrame.CefHost.Tests;

/// <summary>
///     12. Optional, about 35 s, on Windows with a real win-x64 companion payload (<c>CEFHOST_TESTS_PAYLOAD</c>): the provider is created
///     with the public constructor and validates the payload, no test launch. Run it alone with
///     <c>--treenode-filter "/*/*/*/*[Category=RealCompanion]"</c>, or skip it with <c>[Category=Fake]</c>.
/// </summary>
[Category("RealCompanion")]
public sealed class RealCompanionTests {
    [Test]
    [Timeout(90_000)]
    public async Task PartialFrame_IsEndedByTheCompanionsPartialFrameDeadline(CancellationToken cancellationToken) {
        Skip.Unless(OperatingSystem.IsWindows() && TestEnvironment.RealPayload.Length > 0
                && File.Exists(Path.Combine(TestEnvironment.RealPayload, "cef-backend.json")),
            "Set CEFHOST_TESTS_PAYLOAD to a verified win-x64 payload directory (with cef-backend.json) to run this test on Windows.");
        await using HostHarness harness = HostHarness.CreateReal("rc", TestEnvironment.RealPayload);
        await harness.StartAsync();

        // The first 10 bytes of a valid 28-byte query frame: the companion starts its 30 s partial-frame deadline.
        byte[] frame = CefProtocol.Encode(CefKind.Query, 0x7777, 1, 0);
        var clock = Stopwatch.StartNew();
        await harness.Cef.Connection.WriteRawForTestingAsync(frame[..10]);
        Exception? completion = await Wait.ExceptionAsync(() => harness.Host.Completion, "the companion to end the session", TimeSpan.FromSeconds(60));
        TimeSpan endedAfter = clock.Elapsed;
        await harness.ShutdownAsync();
        TimeSpan disposal = await harness.DisposeHostAsync();
        CefHostDiagnostics final = harness.Diagnostics;
        IReadOnlyList<ProcessIdentity> leftovers = await harness.LeftoverProcessesAsync();
        string faults = string.Join(" | ", final.SessionFaults);
        Console.WriteLine($"[real] session ended after {endedAfter.TotalSeconds:0.0} s; disposal {disposal.TotalMilliseconds:0} ms; exit code {final.CompanionExitCode}");

        using (Assert.Multiple()) {
            await Assert.That(completion is InfiniFrameHostOperationException).IsTrue().Because($"Completion: {completion?.GetType().Name}: {completion?.Message}");
            await Assert.That(endedAfter).IsGreaterThan(TimeSpan.FromSeconds(28)).Because("the deadline runs 30 s from the frame's first byte");
            await Assert.That(endedAfter).IsLessThan(TimeSpan.FromSeconds(50));
            await Assert.That(final.CompanionStderrTail.Any(static line => line.Contains("transport partial frame deadline exceeded", StringComparison.Ordinal)))
                .IsTrue().Because("the companion reports why it ended the session");
            await Assert.That(final.SessionFaults.Count).IsGreaterThan(0);
            // The fault InfiniFrame rethrows from Run() (SessionFaults keeps only its first 240 characters).
            await Assert.That(completion?.Message ?? "").Contains("partial frame")
                .Because($"the session fault's stderr summary should name the partial frame; session faults: [{faults}]");
            await Assert.That(final.CompanionExitCode is not null and not 0).IsTrue().Because($"companion exit code {final.CompanionExitCode?.ToString() ?? "unknown"}");
            await Assert.That(final.ProcessTracking).IsEqualTo("job object");
            await Assert.That(final.OwnedProcessesRemaining).IsEqualTo(0);
            await Assert.That(final.Cleanup.StartsWith("clean", StringComparison.Ordinal) || final.Cleanup.StartsWith("removed-after-exit", StringComparison.Ordinal))
                .IsTrue().Because($"cleanup: {final.Cleanup}");
            await Assert.That(leftovers.Count).IsEqualTo(0);
        }
    }
}
