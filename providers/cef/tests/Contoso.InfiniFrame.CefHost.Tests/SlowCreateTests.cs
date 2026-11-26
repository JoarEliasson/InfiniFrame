using System.Diagnostics;
using Contoso.InfiniFrame.CefHost.Tests.Infrastructure;

namespace Contoso.InfiniFrame.CefHost.Tests;

/// <summary>1. A create whose caller gave up before the companion answered.</summary>
[Category("Fake")]
public sealed class SlowCreateTests {
    [Test]
    [Timeout(60_000)]
    public async Task CancelledCreate_IsReclaimedWhenItsLateReplyArrives(CancellationToken cancellationToken) {
        await using HostHarness harness = HostHarness.Create("slow-create", "slow-create", ["--create-delay-ms", "2000"]);
        await harness.StartAsync();
        var sink = new RecordingSink();

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var clock = Stopwatch.StartNew();
        Exception? thrown = await Wait.ExceptionAsync(
            () => harness.Host.CreateWindowAsync(Requests.Window(), sink, cancel.Token).AsTask(), "the cancelled CreateWindowAsync");
        TimeSpan cancelledAfter = clock.Elapsed;
        CefHostDiagnostics afterCancel = harness.Diagnostics;

        // About 2 s after the create, the fake answers; the provider must then close the native window nobody owns.
        FakeLogEntry close = await harness.Fake.WaitForAsync(static entry => entry.IsCommand("close"), "a close command for the orphaned view");
        await Wait.UntilAsync(() => harness.Diagnostics.ViewsClosed == 1, "the reclaimed view's closed event to be processed");
        IReadOnlyList<FakeLogEntry> log = harness.Fake.ReadLog();
        FakeLogEntry create = log.Single(static entry => entry.IsCommand("create"));
        FakeLogEntry lateReply = log.Single(entry => entry.IsSent("ok") && entry.Request == create.Request);
        CefHostDiagnostics afterReclaim = harness.Diagnostics;

        await harness.ShutdownAsync();
        TimeSpan disposal = await harness.DisposeHostAsync();
        CefHostDiagnostics final = harness.Diagnostics;
        IReadOnlyList<ProcessIdentity> leftovers = await harness.LeftoverProcessesAsync();

        using (Assert.Multiple()) {
            await Assert.That(thrown is OperationCanceledException).IsTrue().Because($"CreateWindowAsync threw {thrown?.GetType().Name ?? "nothing"}");
            await Assert.That(cancelledAfter).IsLessThan(TimeSpan.FromSeconds(1.5));
            await Assert.That(afterCancel.CommandsAbandoned).IsEqualTo(1);
            await Assert.That(close.View).IsEqualTo(create.View);
            await Assert.That(close.Seq).IsGreaterThan(lateReply.Seq).Because("the close follows the late create reply");
            await Assert.That(afterReclaim.ViewsReclaimed).IsEqualTo(1);
            await Assert.That(afterReclaim.LateCompletions).IsGreaterThanOrEqualTo(1).Because("the late create reply is a late completion");
            await Assert.That(afterReclaim.LateFaults.Count).IsEqualTo(0);
            await Assert.That(sink.Calls.Count).IsEqualTo(0).Because("an abandoned window raises nothing: no Created, document or Closed");
            await Assert.That(harness.Fake.Received("set-bounds").Count).IsEqualTo(0).Because("the abandoned create is not continued");
            await Assert.That(final.SessionFaults.Count).IsEqualTo(0);
            await Assert.That(final.ExitEscalated).IsFalse();
            await Assert.That(disposal).IsLessThan(harness.Options.ExitTimeout + TimeSpan.FromSeconds(3));
            await Assert.That(leftovers.Count).IsEqualTo(0);
        }
    }
}
