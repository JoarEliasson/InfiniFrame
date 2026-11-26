using Contoso.InfiniFrame.CefHost.Tests.Infrastructure;
using InfiniFrame.Hosting;

namespace Contoso.InfiniFrame.CefHost.Tests;

/// <summary>
///     4. The companion stops reading its input. Nine sends of about 7.9 MiB each (InfiniFrame serializes sends per
///     window, so one per window) exceed the 64 MiB outgoing queue: the provider must fault the session visibly rather
///     than drop or block, and disposal must still be bounded. The stuck companion never sees EOF, so disposal is
///     documented to escalate: terminate the owned tree after <see cref="CefHostOptions.ExitTimeout" /> and report it.
/// </summary>
[Category("Fake")]
public sealed class OutgoingOverflowTests {
    private const int Windows = 9;
    private const int MessageChars = 8_283_750;// 7.9 MiB of ASCII, within the 8 MiB frame budget

    [Test]
    [Timeout(120_000)]
    public async Task OutgoingQueueOverflow_FaultsTheSession_AndDisposalIsBounded(CancellationToken cancellationToken) {
        await using HostHarness harness = HostHarness.Create("overflow", "normal", [], static options => options.ExitTimeout = TimeSpan.FromSeconds(1));
        await harness.StartAsync();
        var windows = new List<(CefWindowHost Window, RecordingSink Sink)>();
        for (int index = 0; index < Windows; index++) windows.Add(await harness.CreateWindowAsync($"window {index + 1}"));

        await windows[0].Window.DirectAsync("stop-reading");
        await harness.Fake.WaitForAsync(static entry => entry.IsLifecycle("stop-reading"), "the fake to stop reading its input");

        string message = new('x', MessageChars);
        Task[] sends = [.. windows.Select(pair => Task.Run(() => pair.Window.SendMessageAsync(new InfiniFrameDocumentId(1), message, CancellationToken.None).AsTask()))];
        Exception?[] failures = await Task.WhenAll(sends.Select((send, index) => Wait.ExceptionAsync(() => send, $"send {index + 1} to be released")));

        Exception? completion = await Wait.ExceptionAsync(() => harness.Host.Completion, "host.Completion to fault");
        CefHostDiagnostics faulted = harness.Diagnostics;
        foreach ((CefWindowHost window, _) in windows) await Wait.WithinAsync(window.DisposeAsync().AsTask(), "window.DisposeAsync");
        await harness.ShutdownAsync();// a no-op on a faulted session, as InfiniFrame may still call it
        TimeSpan disposal = await harness.DisposeHostAsync();
        CefHostDiagnostics final = harness.Diagnostics;
        IReadOnlyList<ProcessIdentity> leftovers = await harness.LeftoverProcessesAsync();

        // The overflowing send fails directly; every caller already waiting gets the contract's operation exception
        // carrying the session's cause ("The CEF companion session failed: …queue limit…"), never a raw IOException.
        Exception[] overflow = [.. failures.OfType<InfiniFrameHostOperationException>().Where(static failure =>
            failure.Message.StartsWith("The CEF outgoing queue limit", StringComparison.Ordinal))];
        Exception[] waiting = [.. failures.OfType<Exception>().Where(static failure => !failure.Message.StartsWith("The CEF outgoing queue limit", StringComparison.Ordinal))];
        string observed = string.Join(" | ", failures.Select(static failure => failure is null ? "completed" : $"{failure.GetType().Name}: {failure.Message}"));
        Console.WriteLine($"[overflow] send outcomes: {observed}");
        Console.WriteLine($"[overflow] disposal {disposal.TotalMilliseconds:0} ms");

        using (Assert.Multiple()) {
            await Assert.That(failures.All(static failure => failure is not null)).IsTrue().Because($"every send fails once the session faults: {observed}");
            await Assert.That(overflow.Length).IsEqualTo(1).Because($"exactly the overflowing send fails directly with the queue limit: {observed}");
            await Assert.That(waiting.All(static failure => failure is InfiniFrameHostOperationException
                    && failure.Message.StartsWith("The CEF companion session failed:", StringComparison.Ordinal) && failure.Message.Contains("queue limit", StringComparison.Ordinal)))
                .IsTrue().Because($"waiting callers get InfiniFrameHostOperationException with the session's cause: {observed}");
            await Assert.That(overflow.FirstOrDefault()?.Message ?? "").Contains("64 MiB");
            await Assert.That(faulted.SessionFaults.Count).IsGreaterThan(0);
            await Assert.That(faulted.SessionFaults.FirstOrDefault() ?? "").Contains("outgoing queue limit");
            await Assert.That(completion is InfiniFrameHostOperationException).IsTrue().Because($"Completion: {completion?.GetType().Name}: {completion?.Message}");
            await Assert.That(completion?.Message ?? "").Contains("queue limit");
            await Assert.That(faulted.PeakQueuedBytes).IsLessThanOrEqualTo(CefProtocol.MaxQueuedBytes + MessageChars + 64L);
            // Disposal: bounded; the stuck companion is terminated (escalation) together with its whole tree.
            await Assert.That(disposal).IsLessThan(TimeSpan.FromSeconds(2) + harness.Options.ExitTimeout + TimeSpan.FromSeconds(5));
            await Assert.That(final.ExitEscalated).IsTrue();
            await Assert.That(final.ExitUnconfirmed).IsFalse();
            await Assert.That(final.OwnedProcessesRemaining).IsEqualTo(0);
            await Assert.That(final.QueuedBytes).IsEqualTo(0);
            await Assert.That(leftovers.Count).IsEqualTo(0);
        }
    }
}
