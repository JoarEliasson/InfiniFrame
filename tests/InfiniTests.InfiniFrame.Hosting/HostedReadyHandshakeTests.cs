// ---------------------------------------------------------------------------------------------------------------------
// Imports
// ---------------------------------------------------------------------------------------------------------------------
using InfiniFrame;
using InfiniFrame.Hosting;
using InfiniFrame.Js.Interop;
using InfiniFrame.Window.Features.WebMessaging.Handlers;
using InfiniFrame.Window.Hosting;

namespace InfiniTests.InfiniFrame.Hosting;
// ---------------------------------------------------------------------------------------------------------------------
// Code
// ---------------------------------------------------------------------------------------------------------------------
[NotInParallel]
public sealed class HostedReadyHandshakeTests {
    // The envelope sent by the bridge, independent of any optional window feature.
    private const string Ready = """{"id":"__infiniframe:ready","command":"Post","data":null,"version":2}""";

    [Test]
    [Timeout(30_000)]
    [Arguments(true)]
    [Arguments(false)]
    public async Task NoOptionalFeatures_ReadyPostIsAcknowledged_EvenWhileCreating(bool created, CancellationToken ct) {
        await using HostedHarness harness = await HostedHarness.StartAsync(new ScriptedHostProvider(InfiniFrameHostCapabilities.None) {
            AutoCreated = created
        });
        (InfiniFrameHostedWindow window, ScriptedWindowHostCore host) = await harness.CreateAsync();
        await host.AttachAsync(1);
        if (!created) await Assert.That(window.LifecycleState).IsEqualTo(InfiniFrameWindowLifecycleState.Creating);
        var acknowledged = new TaskCompletionSource<SentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnSent = sent => {
            if (MessageId(sent) == JsHandlerNames.WindowReadyAck) acknowledged.TrySetResult(sent);
        };

        await host.PostAsync(1, Ready);
        await Task.WhenAny(acknowledged.Task, Task.Delay(2000, ct));
        // The baseline has no ready handler and fails this assertion, rather than merely checking a helper's state.
        await Assert.That(acknowledged.Task.IsCompletedSuccessfully).IsTrue();
        SentMessage ack = await acknowledged.Task;
        InteropEnvelopeParseResult envelope = InteropEnvelopeProtocol.ParseIncomingMessage(ack.Message);
        await Assert.That(envelope.IsSuccess).IsTrue();
        await Assert.That(envelope.Command).IsEqualTo("Post");
        await Assert.That(envelope.Payload).IsNull();
        await Assert.That(ack.Document).IsEqualTo(1UL);
        await Assert.That(ack.ThreadId).IsEqualTo(harness.Host.LoopThreadId);
        await Assert.That(host.Sent.Count).IsEqualTo(1);
        await Assert.That(window.EventsStore.WebMessagePostData.ContainsKey(JsHandlerNames.WindowReady)).IsTrue();

        // Completing the protocol grants no optional title/fullscreen/management/external-opening handlers.
        await Assert.That(window.EventsStore.WebMessagePostData.ContainsKey(JsHandlerNames.TitleChanged)).IsFalse();
        await Assert.That(window.EventsStore.WebMessagePostData.ContainsKey(JsHandlerNames.FullscreenEnter)).IsFalse();
        await Assert.That(window.EventsStore.WebMessagePostData.ContainsKey(JsHandlerNames.WindowClose)).IsFalse();
        await Assert.That(window.EventsStore.WebMessagePostData.ContainsKey(JsHandlerNames.OpenExternal)).IsFalse();
        await Assert.That(() => window.SetFullScreen()).Throws<NotSupportedException>();
    }

    [Test]
    [Timeout(30_000)]
    public async Task OptionalRegistrations_AreDeduplicatedBeforeOneAck_PerCurrentDocument(CancellationToken ct) {
        await using HostedHarness harness = await HostedHarness.StartAsync();
        (InfiniFrameHostedWindow window, ScriptedWindowHostCore host) = await harness.CreateAttachedAsync(1, builder => builder
            .RegisterTitleChangedWebMessageHandler()
            .RegisterTitleChangedWebMessageHandler()
            .RegisterFullScreenWebMessageHandler());

        await host.PostAsync(1, Ready);
        await host.PostAsync(1, Ready);
        await Waits.UntilAsync(() => host.Sent.Any(sent => MessageId(sent) == JsHandlerNames.WindowReadyAck), "first ready ack");
        await host.PostAsync(1, Ready);
        await harness.DrainDispatcherAsync();
        await Task.Delay(50, ct);
        await AssertRegistrationsAsync(host, 1);

        await host.PostAsync(1, InteropEnvelopeProtocol.CreateEnvelopeMessage(JsHandlerNames.TitleChanged, "opted in"));
        await harness.DrainDispatcherAsync();
        await Assert.That(window.Title).IsEqualTo("opted in");

        await host.AttachAsync(2);
        await host.PostAsync(2, Ready);
        await Waits.UntilAsync(() => host.Sent.Any(sent => sent.Document == 2 && MessageId(sent) == JsHandlerNames.WindowReadyAck), "reload ready ack");
        await AssertRegistrationsAsync(host, 2);
        await Assert.That(host.Sent.Count).IsEqualTo(6);
    }

    [Test]
    [Timeout(30_000)]
    public async Task QueuedRetiredAndClosedReadyMessages_CannotAcknowledgeTheReplacement(CancellationToken ct) {
        await using HostedHarness harness = await HostedHarness.StartAsync();
        (InfiniFrameHostedWindow window, ScriptedWindowHostCore host) = await harness.CreateAttachedAsync(1);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<InfiniFrameDispatchResult> blocker = window.DispatchAsync(() => {
            entered.SetResult();
            release.Wait(ct);
        }, TimeSpan.FromSeconds(20), ct).AsTask();
        try {
            await entered.Task.WaitAsync(ct);
            await host.PostAsync(1, Ready);
            await host.AttachAsync(2);
        }
        finally { release.Set(); }
        await blocker;
        await host.PostAsync(1, Ready);
        await host.PostAsync(2, Ready);
        await Waits.UntilAsync(() => !host.Sent.IsEmpty, "current document ready ack");
        SentMessage ack = host.Sent.Single();
        await Assert.That(ack.Document).IsEqualTo(2UL);
        await Assert.That(MessageId(ack)).IsEqualTo(JsHandlerNames.WindowReadyAck);

        await host.RetireAsync(2);
        await host.PostAsync(2, Ready);
        await host.CloseNativelyAsync();
        await window.WaitForTeardownAsync(ct);
        await host.PostAsync(2, Ready);
        await harness.DrainDispatcherAsync();
        await Assert.That(host.Sent.Count).IsEqualTo(1);
    }

    [Test]
    [Timeout(30_000)]
    public async Task InFlightOldRegistration_IsCancelled_AndCannotCompleteTheNewHandshake(CancellationToken ct) {
        await using HostedHarness harness = await HostedHarness.StartAsync();
        (InfiniFrameHostedWindow _, ScriptedWindowHostCore host) = await harness.CreateAttachedAsync(1, builder =>
            builder.RegisterTitleChangedWebMessageHandler());
        var attempted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.SendOverride = async (document, message, token) => {
            if (document.Value == 1) {
                attempted.TrySetResult(token);
                using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult());
                await release.Task.WaitAsync(ct);
                throw new InfiniFrameHostDocumentRetiredException(document);
            }
            await host.Host.OnLoopAsync(() => {
                token.ThrowIfCancellationRequested();
                if (document.Value != host.ProviderDocument) throw new InfiniFrameHostDocumentRetiredException(document);
                host.Sent.Enqueue(new SentMessage(document.Value, message, Environment.CurrentManagedThreadId));
            });
        };
        try {
            await host.PostAsync(1, Ready);
            await attempted.Task.WaitAsync(ct);
            await host.PostAsync(1, Ready); // A second ready during the send must not create another transaction.
            await host.AttachAsync(2);
            await host.PostAsync(2, Ready);
            await harness.DrainDispatcherAsync();
            await cancelled.Task.WaitAsync(ct);
        }
        finally { release.TrySetResult(); }
        await Waits.UntilAsync(() => host.Sent.Any(sent => MessageId(sent) == JsHandlerNames.WindowReadyAck), "replacement handshake");
        SentMessage[] sent = [.. host.Sent];
        await Assert.That(sent.Length).IsEqualTo(2);
        await Assert.That(sent.All(message => message.Document == 2)).IsTrue();
        await Assert.That(MessageId(sent[0])).IsEqualTo(JsHandlerNames.RegisterTitleChange);
        await Assert.That(MessageId(sent[1])).IsEqualTo(JsHandlerNames.WindowReadyAck);
    }

    [Test]
    [Timeout(30_000)]
    public async Task CloseRequestedDuringRegistration_BlocksAck_AndVetoAllowsRetry(CancellationToken ct) {
        await using HostedHarness harness = await HostedHarness.StartAsync();
        (InfiniFrameHostedWindow window, ScriptedWindowHostCore host) = await harness.CreateAttachedAsync(1, builder =>
            builder.RegisterTitleChangedWebMessageHandler());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        host.SendOverride = async (document, message, token) => {
            if (Interlocked.Increment(ref calls) == 1) {
                started.SetResult();
                await release.Task.WaitAsync(ct);
            }
            await host.Host.OnLoopAsync(() => {
                token.ThrowIfCancellationRequested();
                host.Sent.Enqueue(new SentMessage(document.Value, message, Environment.CurrentManagedThreadId));
            });
        };
        try {
            await host.PostAsync(1, Ready);
            await started.Task.WaitAsync(ct);
            await Assert.That(((IInfiniFrameWindow)window).RequestClose()).IsTrue();
        }
        finally { release.TrySetResult(); }
        await Waits.UntilAsync(() => !host.Sent.IsEmpty, "in-flight registration completion");
        await Task.Delay(50, ct);
        await Assert.That(window.CurrentDocument).IsEqualTo(1UL);
        await Assert.That(host.Sent.Count).IsEqualTo(1);
        await Assert.That(MessageId(host.Sent.Single())).IsEqualTo(JsHandlerNames.RegisterTitleChange);

        ((IInfiniFrameWindow)window).CancelCloseRequest();
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        do {
            await host.PostAsync(1, Ready);
            await Task.Delay(5, ct);
        } while (!host.Sent.Any(sent => MessageId(sent) == JsHandlerNames.WindowReadyAck) && DateTime.UtcNow < deadline);
        await Assert.That(host.Sent.Count(sent => MessageId(sent) == JsHandlerNames.WindowReadyAck)).IsEqualTo(1);
    }

    private static string? MessageId(SentMessage sent) => InteropEnvelopeProtocol.ParseIncomingMessage(sent.Message).MessageId;

    private static async Task AssertRegistrationsAsync(ScriptedWindowHostCore host, ulong document) {
        SentMessage[] sent = [.. host.Sent.Where(message => message.Document == document)];
        await Assert.That(sent.Length).IsEqualTo(3);
        await Assert.That(sent.Count(message => MessageId(message) == JsHandlerNames.RegisterTitleChange)).IsEqualTo(1);
        await Assert.That(sent.Count(message => MessageId(message) == JsHandlerNames.RegisterFullScreenChange)).IsEqualTo(1);
        await Assert.That(MessageId(sent[^1])).IsEqualTo(JsHandlerNames.WindowReadyAck);
    }
}
