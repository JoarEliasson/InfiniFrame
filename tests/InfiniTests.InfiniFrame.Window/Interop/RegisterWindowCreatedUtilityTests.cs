// ---------------------------------------------------------------------------------------------------------------------
// Imports
// ---------------------------------------------------------------------------------------------------------------------
using InfiniFrame.Js.Interop;
using InfiniFrame.NativeBridge.Parameters.Window;
using InfiniFrame.Window.Builder;
using InfiniFrame.Window.Events;
using InfiniFrame.Window.Interop;
using InfiniTests.Substitutes;
using Microsoft.Extensions.Logging.Abstractions;

namespace InfiniTests.InfiniFrame.Window.Interop;
// ---------------------------------------------------------------------------------------------------------------------
// Code
// ---------------------------------------------------------------------------------------------------------------------
public class RegisterWindowCreatedUtilityTests {
    [Test]
    [Timeout(10_000)]
    public async Task DefaultBuilder_WithoutOptionalFeatures_AcknowledgesReady(CancellationToken ct) {
        var builder = InfiniFrameWindowBuilder.Create();
        RecordingInfiniFrameWindowSubstitute window = new RecordingInfiniFrameWindowSubstitute().BindToBuilder(builder);
        var events = new InfiniFrameEvents(new InfiniFrameEventsStore(), NullLogger<InfiniFrameEvents>.Instance);
        events.PopulateFromBuilderEventStore(builder.EventsStore);
        events.AssignToWindow(window.Window);
        var nativeParameters = default(InfiniFrameNativeWindowParameters);
        events.AssignToNativeParameters(ref nativeParameters);

        // Exercise normal builder/event setup without manually invoking the registration utility or WindowCreated.
        events.OnWebMessageReceived("""{"id":"__infiniframe:ready","command":"Post","data":null,"version":2}""");
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (window.CountEnvelopeMessagesById("__infiniframe:ready:ack") == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(5, ct);

        await Assert.That(window.CountEnvelopeMessagesById("__infiniframe:ready:ack")).IsEqualTo(1);
        await Assert.That(window.GetSentMessagesSnapshot().Count).IsEqualTo(1);
        await Assert.That(events.EventsStore.WebMessagePostData.ContainsKey(JsHandlerNames.TitleChanged)).IsFalse();
    }

    [Test]
    [Timeout(10_000)]
    public async Task FailedReadyAck_CanRetry_WithoutEnablingOptionalFeatures(CancellationToken ct) {
        var builder = InfiniFrameWindowBuilder.Create();
        RecordingInfiniFrameWindowSubstitute window = new RecordingInfiniFrameWindowSubstitute().BindToBuilder(builder);
        var events = new InfiniFrameEvents(new InfiniFrameEventsStore(), NullLogger<InfiniFrameEvents>.Instance);
        events.PopulateFromBuilderEventStore(builder.EventsStore);
        events.AssignToWindow(window.Window);
        int sendAttempts = 0;
        window.WebMessaging.SendWebMessageAsync(Any<string>(), Any<CancellationToken>())
            .Returns(() => Interlocked.Increment(ref sendAttempts) == 1
                ? ValueTask.FromException(new InvalidOperationException("First transport submission failed"))
                : ValueTask.CompletedTask);

        string ready = InteropEnvelopeProtocol.CreateEnvelopeMessage(JsHandlerNames.WindowReady);
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        do {
            events.OnWebMessageReceived(ready);
            await Task.Delay(5, ct);
        } while (Volatile.Read(ref sendAttempts) < 2 && DateTime.UtcNow < deadline);

        await Assert.That(Volatile.Read(ref sendAttempts)).IsEqualTo(2);
        events.OnWebMessageReceived(ready);
        await Task.Delay(50, ct);
        await Assert.That(Volatile.Read(ref sendAttempts)).IsEqualTo(2);
    }

    [Test]
    public async Task Registration_IsGatedByWindowReadyHandshake(CancellationToken ct = default) {
        // Arrange
        const string registrationMessageId = "__infiniframe:register:test";
        string readyEnvelope = InteropEnvelopeProtocol.CreateEnvelopeMessage("__infiniframe:ready");
        var builder = InfiniFrameWindowBuilder.Create();
        var eventsStore = (InfiniFrameEventsStore)builder.EventsStore;
        var events = new InfiniFrameEvents(eventsStore, NullLogger<InfiniFrameEvents>.Instance);
        RecordingInfiniFrameWindowSubstitute window = new RecordingInfiniFrameWindowSubstitute()
            .BindToBuilder(builder);

        var nativeParameters = default(InfiniFrameNativeWindowParameters);
        events.AssignToNativeParameters(ref nativeParameters);
        events.AssignToWindow(window.Window);

        RegisterWindowCreatedUtility.RegisterWindowCreatedWebMessage(builder, registrationMessageId);

        // Act
        events.OnWindowCreated();
        await Task.Delay(150, ct);

        // Assert pre-ready: nothing should be sent yet.
        int sendAttemptsBeforeReady = window.CountEnvelopeMessagesById(registrationMessageId);
        await Assert.That(sendAttemptsBeforeReady).IsEqualTo(0);

        // Act: explicit ready handshake.
        events.OnWebMessageReceived(readyEnvelope);
        await Task.Delay(150, ct);

        // Assert: exactly one registration send attempt after ready.
        int sendAttemptsAfterReady = window.CountEnvelopeMessagesById(registrationMessageId);
        await Assert.That(sendAttemptsAfterReady).IsEqualTo(1);

        int ackAttemptsAfterReady = window.CountEnvelopeMessagesById("__infiniframe:ready:ack");
        await Assert.That(ackAttemptsAfterReady).IsEqualTo(1);
    }

    [Test]
    public async Task Registration_IsIdempotentAcrossRepeatedReadyMessages(CancellationToken ct = default) {
        // Arrange
        const string registrationMessageId = "__infiniframe:register:test";
        string readyEnvelope = InteropEnvelopeProtocol.CreateEnvelopeMessage("__infiniframe:ready");
        var builder = InfiniFrameWindowBuilder.Create();
        var eventsStore = (InfiniFrameEventsStore)builder.EventsStore;
        var events = new InfiniFrameEvents(eventsStore, NullLogger<InfiniFrameEvents>.Instance);
        RecordingInfiniFrameWindowSubstitute window = new RecordingInfiniFrameWindowSubstitute()
            .BindToBuilder(builder);

        var nativeParameters = default(InfiniFrameNativeWindowParameters);
        events.AssignToNativeParameters(ref nativeParameters);
        events.AssignToWindow(window.Window);

        RegisterWindowCreatedUtility.RegisterWindowCreatedWebMessage(builder, registrationMessageId);

        // Act: ready envelope received multiple times.
        events.OnWebMessageReceived(readyEnvelope);
        events.OnWebMessageReceived(readyEnvelope);
        await Task.Delay(150, ct);

        // Assert: only one registration send attempt.
        int sendAttempts = window.CountEnvelopeMessagesById(registrationMessageId);
        await Assert.That(sendAttempts).IsEqualTo(1);
    }

    [Test]
    public async Task Registration_AcknowledgementIsSentAfterRegistrations(CancellationToken ct = default) {
        // Arrange
        const string registrationMessageId = "__infiniframe:register:test";
        string readyEnvelope = InteropEnvelopeProtocol.CreateEnvelopeMessage("__infiniframe:ready");
        var builder = InfiniFrameWindowBuilder.Create();
        var eventsStore = (InfiniFrameEventsStore)builder.EventsStore;
        var events = new InfiniFrameEvents(eventsStore, NullLogger<InfiniFrameEvents>.Instance);
        RecordingInfiniFrameWindowSubstitute window = new RecordingInfiniFrameWindowSubstitute()
            .BindToBuilder(builder);

        var nativeParameters = default(InfiniFrameNativeWindowParameters);
        events.AssignToNativeParameters(ref nativeParameters);
        events.AssignToWindow(window.Window);

        RegisterWindowCreatedUtility.RegisterWindowCreatedWebMessage(builder, registrationMessageId);

        // Act
        events.OnWindowCreated();
        events.OnWebMessageReceived(readyEnvelope);
        await Task.Delay(150, ct);

        // Assert
        IReadOnlyList<string> sentMessages = window.GetSentMessagesSnapshot();
        int registrationIndex = FindMessageIndex(sentMessages, registrationMessageId);
        int ackIndex = FindMessageIndex(sentMessages, "__infiniframe:ready:ack");

        await Assert.That(registrationIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(ackIndex).IsGreaterThan(registrationIndex);
    }

    private static int FindMessageIndex(IReadOnlyList<string> sentMessages, string messageId)
        => sentMessages
            .Select((message, index) => new {
                ParseResult = InteropEnvelopeProtocol.ParseIncomingMessage(message),
                Index = index
            })
            .Where(item => item.ParseResult.IsSuccess && string.Equals(item.ParseResult.MessageId, messageId, StringComparison.Ordinal))
            .Select(item => item.Index)
            .FirstOrDefault(-1);
}
