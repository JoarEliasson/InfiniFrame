using Contoso.InfiniFrame.CefHost.Tests.Infrastructure;
using InfiniFrame.Hosting;

namespace Contoso.InfiniFrame.CefHost.Tests;

/// <summary>
///     2. Commands whose caller stopped waiting: the companion still answers them later (here, only once
///     <c>ShutdownAsync</c> has begun, because the fake holds the reply until it receives the shutdown command), or
///     never. Every step and the disposal are bounded.
/// </summary>
[Category("Fake")]
public sealed class AbandonedCommandTests {
    private const string HeldTitle = "title whose reply is held";

    [Test]
    [Timeout(60_000)]
    public async Task LateOkReply_AfterShutdownBegan_CountsAsLateCompletion(CancellationToken cancellationToken) {
        await using HostHarness harness = HostHarness.Create("late-ok", "normal",
            ["--hold", "set-title", "--release-on", "shutdown", "--late-reply", "ok"]);
        await harness.StartAsync();
        (CefWindowHost window, RecordingSink sink) = await harness.CreateWindowAsync();

        Exception? thrown = await AbandonAsync(token => window.SetTitleAsync(HeldTitle, token).AsTask());
        await harness.Fake.WaitForAsync(static entry => entry.IsAction("held") && entry.Kind == "set-title", "the held set-title");
        CefHostDiagnostics abandoned = harness.Diagnostics;
        await HostHarness.CloseWindowAsync(window, sink);
        long before = harness.Diagnostics.LateCompletions;

        TimeSpan shutdown = await harness.ShutdownAsync();
        CefHostDiagnostics after = harness.Diagnostics;
        (long shutdownSeq, long releaseSeq) = ShutdownAndRelease(harness);
        TimeSpan disposal = await harness.DisposeHostAsync();
        CefHostDiagnostics final = harness.Diagnostics;
        IReadOnlyList<ProcessIdentity> leftovers = await harness.LeftoverProcessesAsync();

        using (Assert.Multiple()) {
            await Assert.That(thrown is OperationCanceledException).IsTrue().Because($"SetTitleAsync threw {thrown?.GetType().Name ?? "nothing"}");
            await Assert.That(abandoned.CommandsAbandoned).IsEqualTo(1);
            await Assert.That(abandoned.PendingCommands).IsEqualTo(1).Because("an abandoned command keeps its slot until answered");
            await Assert.That(releaseSeq).IsGreaterThan(shutdownSeq).Because("the fake answers only after the shutdown command arrived");
            await Assert.That(after.LateCompletions).IsEqualTo(before + 1);
            await Assert.That(after.LateFaults.Count).IsEqualTo(0);
            await Assert.That(after.PendingCommands).IsEqualTo(0);
            await Assert.That(shutdown).IsLessThan(TimeSpan.FromSeconds(5));
            await Assert.That(disposal).IsLessThan(harness.Options.ExitTimeout + TimeSpan.FromSeconds(3));
            await Assert.That(final.ExitEscalated).IsFalse();
            await Assert.That(final.SessionFaults.Count).IsEqualTo(0);
            await Assert.That(harness.Host.Completion.IsCompletedSuccessfully).IsTrue();
            await Assert.That(leftovers.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task LateErrorReply_AfterShutdownBegan_IsRecordedAsLateFaultWithItsReason(CancellationToken cancellationToken) {
        const string reason = "fake: title rejected after its caller gave up";
        await using HostHarness harness = HostHarness.Create("late-error", "normal",
            ["--hold", "set-title", "--release-on", "shutdown", "--late-reply", "error", "--late-error", reason]);
        await harness.StartAsync();
        (CefWindowHost window, RecordingSink sink) = await harness.CreateWindowAsync();

        Exception? thrown = await AbandonAsync(token => window.SetTitleAsync(HeldTitle, token).AsTask());
        await harness.Fake.WaitForAsync(static entry => entry.IsAction("held") && entry.Kind == "set-title", "the held set-title");
        await HostHarness.CloseWindowAsync(window, sink);
        long completionsBefore = harness.Diagnostics.LateCompletions;

        await harness.ShutdownAsync();
        CefHostDiagnostics after = harness.Diagnostics;
        (long shutdownSeq, long releaseSeq) = ShutdownAndRelease(harness);
        TimeSpan disposal = await harness.DisposeHostAsync();
        CefHostDiagnostics final = harness.Diagnostics;
        IReadOnlyList<ProcessIdentity> leftovers = await harness.LeftoverProcessesAsync();
        string[] titleFaults = [.. after.LateFaults.Where(static fault => fault.StartsWith("set-title", StringComparison.Ordinal))];

        using (Assert.Multiple()) {
            await Assert.That(thrown is OperationCanceledException).IsTrue().Because($"SetTitleAsync threw {thrown?.GetType().Name ?? "nothing"}");
            await Assert.That(releaseSeq).IsGreaterThan(shutdownSeq);
            await Assert.That(titleFaults.Length).IsEqualTo(1).Because($"late faults: [{string.Join(" | ", after.LateFaults)}]");
            await Assert.That(titleFaults.FirstOrDefault() ?? "").Contains("(caller-cancelled)");
            await Assert.That(titleFaults.FirstOrDefault() ?? "").Contains(reason);
            await Assert.That(after.LateCompletions).IsEqualTo(completionsBefore).Because("an error reply is a fault, not a completion");
            await Assert.That(after.LateRetiredRejections).IsEqualTo(0);
            await Assert.That(disposal).IsLessThan(harness.Options.ExitTimeout + TimeSpan.FromSeconds(3));
            await Assert.That(final.ExitEscalated).IsFalse();
            await Assert.That(final.SessionFaults.Count).IsEqualTo(0).Because("a late per-command error does not fault the session");
            await Assert.That(leftovers.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task LateDocumentRetiredErrorForAbandonedSend_IsARetiredRejectionNotAFault(CancellationToken cancellationToken) {
        await using HostHarness harness = HostHarness.Create("late-retired", "normal",
            ["--hold", "send", "--release-on", "shutdown", "--late-reply", "error", "--late-error", "CEF_DOCUMENT_RETIRED"]);
        await harness.StartAsync();
        (CefWindowHost window, RecordingSink sink) = await harness.CreateWindowAsync();
        var document = new InfiniFrameDocumentId(sink.Named(nameof(RecordingSink.DocumentAttached))[0].Document);

        Exception? thrown = await AbandonAsync(token => window.SendMessageAsync(document, "message whose reply is held", token).AsTask());
        await harness.Fake.WaitForAsync(static entry => entry.IsAction("held") && entry.Kind == "send", "the held send");
        await HostHarness.CloseWindowAsync(window, sink);
        long completionsBefore = harness.Diagnostics.LateCompletions;

        await harness.ShutdownAsync();
        CefHostDiagnostics after = harness.Diagnostics;
        (long shutdownSeq, long releaseSeq) = ShutdownAndRelease(harness);
        TimeSpan disposal = await harness.DisposeHostAsync();
        CefHostDiagnostics final = harness.Diagnostics;
        IReadOnlyList<ProcessIdentity> leftovers = await harness.LeftoverProcessesAsync();

        using (Assert.Multiple()) {
            await Assert.That(thrown is OperationCanceledException).IsTrue().Because($"SendMessageAsync threw {thrown?.GetType().Name ?? "nothing"}");
            await Assert.That(releaseSeq).IsGreaterThan(shutdownSeq);
            await Assert.That(after.LateRetiredRejections).IsEqualTo(1);
            await Assert.That(after.LateFaults.Count).IsEqualTo(0).Because($"late faults: [{string.Join(" | ", after.LateFaults)}]");
            await Assert.That(after.LateCompletions).IsEqualTo(completionsBefore);
            await Assert.That(disposal).IsLessThan(harness.Options.ExitTimeout + TimeSpan.FromSeconds(3));
            await Assert.That(final.SessionFaults.Count).IsEqualTo(0);
            await Assert.That(final.ExitEscalated).IsFalse();
            await Assert.That(leftovers.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task AbandonedCommandNeverAnswered_IsReportedAtSessionEnd_AndDisposalIsBounded(CancellationToken cancellationToken) {
        await using HostHarness harness = HostHarness.Create("never-answered", "normal", ["--hold", "set-title", "--release-on", "never"]);
        await harness.StartAsync();
        (CefWindowHost window, RecordingSink sink) = await harness.CreateWindowAsync();

        Exception? thrown = await AbandonAsync(token => window.SetTitleAsync(HeldTitle, token).AsTask());
        await harness.Fake.WaitForAsync(static entry => entry.IsAction("held") && entry.Kind == "set-title", "the held set-title");
        await HostHarness.CloseWindowAsync(window, sink);
        TimeSpan shutdown = await harness.ShutdownAsync();
        CefHostDiagnostics beforeDisposal = harness.Diagnostics;
        TimeSpan disposal = await harness.DisposeHostAsync();
        CefHostDiagnostics final = harness.Diagnostics;
        IReadOnlyList<ProcessIdentity> leftovers = await harness.LeftoverProcessesAsync();

        using (Assert.Multiple()) {
            await Assert.That(thrown is OperationCanceledException).IsTrue().Because($"SetTitleAsync threw {thrown?.GetType().Name ?? "nothing"}");
            await Assert.That(beforeDisposal.PendingCommands).IsEqualTo(1).Because("the abandoned command still holds its slot");
            await Assert.That(shutdown).IsLessThan(TimeSpan.FromSeconds(5)).Because("shutdown does not wait for abandoned commands");
            await Assert.That(final.AbandonedAtSessionEnd).IsEqualTo(1);
            await Assert.That(final.LateFaults.Any(static fault => fault.Contains("unanswered at disposed", StringComparison.Ordinal))).IsTrue()
                .Because($"late faults: [{string.Join(" | ", final.LateFaults)}]");
            await Assert.That(disposal).IsLessThan(harness.Options.ExitTimeout + TimeSpan.FromSeconds(3));
            await Assert.That(final.ExitEscalated).IsFalse();
            await Assert.That(final.PendingCommands).IsEqualTo(0);
            await Assert.That(leftovers.Count).IsEqualTo(0);
        }
    }

    /// <summary>Starts a command, gives up waiting after 200 ms, and returns what the call threw.</summary>
    private static async Task<Exception?> AbandonAsync(Func<CancellationToken, Task> command) {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        return await Wait.ExceptionAsync(() => command(cancel.Token), "the abandoned command's caller to be released");
    }

    /// <summary>Sequence numbers of the shutdown command's arrival and of the fake releasing the held reply.</summary>
    private static (long Shutdown, long Release) ShutdownAndRelease(HostHarness harness) {
        IReadOnlyList<FakeLogEntry> log = harness.Fake.ReadLog();
        long shutdown = log.FirstOrDefault(static entry => entry.IsCommand("shutdown"))?.Seq ?? -1;
        long release = log.FirstOrDefault(static entry => entry.IsAction("releasing"))?.Seq ?? -1;
        return (shutdown, release);
    }
}
