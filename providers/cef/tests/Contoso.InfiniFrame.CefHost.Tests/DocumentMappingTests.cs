using Contoso.InfiniFrame.CefHost.Tests.Infrastructure;
using InfiniFrame.Hosting;

namespace Contoso.InfiniFrame.CefHost.Tests;

/// <summary>
///     8. The companion's native document generations are opaque, non-monotonic 64-bit values; the provider maps them
///     to public ids 1, 2, 3… per window, in attach order, and accepts traffic only for the current one. The fake
///     attaches <c>0xDEADBEEF</c> and then the smaller <c>0x1234</c>.
/// </summary>
[Category("Fake")]
public sealed class DocumentMappingTests {
    private const ulong First = 0xDEADBEEF;
    private const ulong Second = 0x1234;

    [Test]
    [Timeout(60_000)]
    public async Task NonMonotonicNativeGenerations_BecomePublicIds1Then2(CancellationToken cancellationToken) {
        await using HostHarness harness = Create("doc-ids");
        await harness.StartAsync();
        (CefWindowHost window, RecordingSink sink) = await harness.CreateWindowAsync(waitForDocument: false);

        await window.DirectAsync($"attach 0x{First:X} app://localhost/first.html");
        await window.DirectAsync($"attach 0x{Second:X} app://localhost/second.html");
        await sink.WaitForAsync(nameof(RecordingSink.DocumentAttached), 2);
        // A send to the current public document reaches the companion with the native generation behind it.
        await Wait.WithinAsync(window.SendMessageAsync(new InfiniFrameDocumentId(2), "to the second document", CancellationToken.None).AsTask(), "a send to document 2");
        FakeLogEntry send = harness.Fake.Received("send").Single();
        CefHostDiagnostics state = harness.Diagnostics;
        IReadOnlyList<SinkCall> attached = sink.Named(nameof(RecordingSink.DocumentAttached));
        await Finish(harness, window, sink);

        using (Assert.Multiple()) {
            await Assert.That(string.Join(",", attached.Select(static call => call.Document))).IsEqualTo("1,2").Because("public ids in attach order");
            await Assert.That(attached[0].Text).IsEqualTo("app://localhost/first.html");
            await Assert.That(attached[1].Text).IsEqualTo("app://localhost/second.html");
            await Assert.That(send.Generation).IsEqualTo(Second);
            await Assert.That(state.DocumentsAttached).IsEqualTo(2);
            await Assert.That(state.SessionFaults.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task MessageFromReplacedDocument_IsDroppedBeforeReachingInfiniFrame(CancellationToken cancellationToken) {
        await using HostHarness harness = Create("doc-stale");
        await harness.StartAsync();
        (CefWindowHost window, RecordingSink sink) = await harness.CreateWindowAsync(waitForDocument: false);

        await window.DirectAsync($"attach 0x{First:X}");
        await window.DirectAsync($"message 0x{First:X} first document, still current");
        await window.DirectAsync($"attach 0x{Second:X}");
        await window.DirectAsync($"message 0x{First:X} stale: from the replaced document");
        long droppedAfterStale = harness.Diagnostics.StaleMessagesDropped;
        await window.DirectAsync($"message 0x{Second:X} second document");
        // Delivery is ordered: once the second document's message arrived, the stale one was handled before it.
        await sink.WaitForAsync(nameof(RecordingSink.MessageReceived), 2);
        IReadOnlyList<SinkCall> messages = sink.Named(nameof(RecordingSink.MessageReceived));
        CefHostDiagnostics state = harness.Diagnostics;
        await Finish(harness, window, sink);

        using (Assert.Multiple()) {
            await Assert.That(messages.Count).IsEqualTo(2);
            await Assert.That(string.Join(" | ", messages.Select(static call => $"{call.Document}: {call.Text}")))
                .IsEqualTo("1: first document, still current | 2: second document");
            await Assert.That(droppedAfterStale).IsEqualTo(1).Because("the stale message is dropped as it is read");
            await Assert.That(state.StaleMessagesDropped).IsEqualTo(1);
            await Assert.That(state.SessionFaults.Count).IsEqualTo(0).Because("a stale message is expected traffic, not corruption");
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task SendToReplacedDocument_ThrowsDocumentRetired(CancellationToken cancellationToken) {
        await using HostHarness harness = Create("doc-retired");
        await harness.StartAsync();
        (CefWindowHost window, RecordingSink sink) = await harness.CreateWindowAsync(waitForDocument: false);

        await window.DirectAsync($"attach 0x{First:X}");
        await window.DirectAsync($"attach 0x{Second:X}");
        Exception? thrown = await Wait.ExceptionAsync(
            () => window.SendMessageAsync(new InfiniFrameDocumentId(1), "to the replaced document", CancellationToken.None).AsTask(), "a send to document 1");
        int sendsReceived = harness.Fake.Received("send").Count;
        CefHostDiagnostics state = harness.Diagnostics;
        await Finish(harness, window, sink);

        using (Assert.Multiple()) {
            await Assert.That(thrown is InfiniFrameHostDocumentRetiredException).IsTrue().Because($"SendMessageAsync threw {thrown?.GetType().Name ?? "nothing"}");
            await Assert.That((thrown as InfiniFrameHostDocumentRetiredException)?.Document.Value).IsEqualTo(1UL);
            await Assert.That(sendsReceived).IsEqualTo(0).Because("the message is never delivered to any other document");
            await Assert.That(state.SessionFaults.Count).IsEqualTo(0);
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task SecondAttachOfAnAlreadySeenGeneration_FaultsTheSession(CancellationToken cancellationToken) {
        await using HostHarness harness = Create("doc-twice");
        await harness.StartAsync();
        (CefWindowHost window, RecordingSink sink) = await harness.CreateWindowAsync(waitForDocument: false);

        await window.DirectAsync($"attach 0x{First:X}");
        await window.DirectAsync($"attach 0x{Second:X}");
        Exception? directive = await Wait.ExceptionAsync(() => window.DirectAsync($"attach 0x{First:X}"), "the repeated attach");
        Exception? completion = await Wait.ExceptionAsync(() => harness.Host.Completion, "host.Completion to fault");
        CefHostDiagnostics state = harness.Diagnostics;
        await Wait.WithinAsync(window.DisposeAsync().AsTask(), "window.DisposeAsync");
        await harness.ShutdownAsync();
        await harness.DisposeHostAsync();
        IReadOnlyList<ProcessIdentity> leftovers = await harness.LeftoverProcessesAsync();

        using (Assert.Multiple()) {
            await Assert.That(state.SessionFaults.FirstOrDefault())
                .IsEqualTo("InvalidDataException: The CEF companion attached a native document generation twice.");
            await Assert.That(completion is InfiniFrameHostOperationException).IsTrue().Because($"Completion: {completion?.GetType().Name}: {completion?.Message}");
            await Assert.That(directive).IsNotNull().Because("the command waiting behind the corrupt frame fails with the session");
            await Assert.That(sink.Named(nameof(RecordingSink.DocumentAttached)).Count).IsEqualTo(2);
            await Assert.That(leftovers.Count).IsEqualTo(0);
        }
    }

    private static HostHarness Create(string tag) => HostHarness.Create(tag, "normal", ["--no-auto-attach"]);

    private static async Task Finish(HostHarness harness, CefWindowHost window, RecordingSink sink) {
        await HostHarness.CloseWindowAsync(window, sink);
        await harness.ShutdownAsync();
        await harness.DisposeHostAsync();
        IReadOnlyList<ProcessIdentity> leftovers = await harness.LeftoverProcessesAsync();
        if (leftovers.Count != 0) throw new InvalidOperationException($"Leftover processes: {string.Join(", ", leftovers)}");
    }
}
