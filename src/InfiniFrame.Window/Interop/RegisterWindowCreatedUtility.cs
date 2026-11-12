// ---------------------------------------------------------------------------------------------------------------------
// Imports
// ---------------------------------------------------------------------------------------------------------------------
using System.Runtime.CompilerServices;
using InfiniFrame.Hosting;
using InfiniFrame.Js.Interop;
using InfiniFrame.Utilities;
using Microsoft.Extensions.Logging;

namespace InfiniFrame.Window.Interop;
// ---------------------------------------------------------------------------------------------------------------------
// Code
// ---------------------------------------------------------------------------------------------------------------------
/// <summary>
///     Provides utility methods for registering web messages that are sent automatically when a window is created and
///     ready.
/// </summary>
public static class RegisterWindowCreatedUtility {
    private static readonly ConditionalWeakTable<IInfiniFrameWindowBuilder, WindowReadyRegistrationState> RegistrationStates = new();

    // -----------------------------------------------------------------------------------------------------------------
    // Methods
    // -----------------------------------------------------------------------------------------------------------------
    /// <summary>Installs the mandatory bridge handshake without enabling any optional feature registrations.</summary>
    internal static void EnsureReadyHandshake(IInfiniFrameWindowBuilder builder) {
        WindowReadyRegistrationState state = RegistrationStates.GetOrCreateValue(builder);
        EnsureWindowCreatedHandler(builder, state);
        EnsureReadyHandler(builder, state);
    }

    /// <summary>
    ///     Registers a web message to be sent to a new window when it signals readiness.
    /// </summary>
    /// <param name="builder">The window builder associated with the registration.</param>
    /// <param name="messageId">The identifier of the message to send to the window on ready.</param>
    public static void RegisterWindowCreatedWebMessage(IInfiniFrameWindowBuilder builder, string messageId) {
        WindowReadyRegistrationState registrationState = RegistrationStates.GetOrCreateValue(builder);

        lock (registrationState.Lock) {
            registrationState.RegistrationMessageIds.Add(messageId);
        }

        EnsureWindowCreatedHandler(builder, registrationState);
        EnsureReadyHandler(builder, registrationState);
    }

    private static void EnsureWindowCreatedHandler(IInfiniFrameWindowBuilder builder, WindowReadyRegistrationState state) {
        lock (state.Lock) {
            if (state.WindowCreatedHandlerRegistered) return;

            state.WindowCreatedHandlerRegistered = true;
        }

        builder.EventsStore.WindowCreated.Add(window => {
            lock (state.Lock) {
                state.Windows.GetOrCreateValue(window);
            }
        });
    }

    private static void EnsureReadyHandler(IInfiniFrameWindowBuilder builder, WindowReadyRegistrationState state) {
        lock (state.Lock) {
            if (state.ReadyHandlerRegistered) return;

            state.ReadyHandlerRegistered = true;
        }

        builder.RegisterWebMessagePostHandler(JsHandlerNames.WindowReady, handler: (window, payload) => {
            if (window.IsClosedOrClosing()) return;
            ulong? document = null;
            if (window is IInfiniFrameDocumentWindow documentWindow) {
                // Use the producing callback's identity, never whichever document happens to be current later.
                if (documentWindow.IncomingMessageDocument is not {} source
                    || documentWindow.GetDocumentLifetime(source).IsCancellationRequested) return;
                document = source;
            }

            WindowRegistrationState windowState;
            string[] registrationMessages;
            lock (state.Lock) {
                windowState = state.Windows.GetOrCreateValue(window);
                if (windowState.Document != document) {
                    // Keep only the latest document's state. In-flight sends retain their own state, so their
                    // completion cannot acknowledge or reset the replacement document's handshake.
                    windowState = new WindowRegistrationState { Document = document };
                    state.Windows.Remove(window);
                    state.Windows.Add(window, windowState);
                }
                if (!windowState.StateMachine.TryBeginRegistrationSendOnReady()) return;

                registrationMessages = [.. state.RegistrationMessageIds];
            }

            ILogger? logger = (ILogger?)window.ServiceProvider?.GetService(typeof(ILogger));
            _ = Task.Run(async () => {
                try {
                    await SendRegistrationsAndAckAsync(window, state, windowState, registrationMessages).ConfigureAwait(false);
                }
                catch (Exception ex) when (ExceptionsUtility.IsNonFatalException(ex)) {
                    logger?.LogWarning(ex, "Unhandled error while sending window-created registration messages");
                }
            });
        });
    }

    private static async Task SendRegistrationsAndAckAsync(
        IInfiniFrameWindow window,
        WindowReadyRegistrationState state,
        WindowRegistrationState windowState,
        IReadOnlyList<string> registrationMessages
    ) {
        bool allMessagesSent = false;
        try {
            allMessagesSent = await SendRegistrationsAndAckAsync(window, windowState.Document, registrationMessages).ConfigureAwait(false);
        }
        catch (Exception ex) when (ExceptionsUtility.IsNonFatalException(ex)) {
            // Non-fatal: registration messages may fail if the window is closing.
        }
        finally {
            lock (state.Lock) {
                windowState.StateMachine.CompleteRegistrationSend(allMessagesSent);
            }
        }
    }

    private static async Task<bool> SendRegistrationsAndAckAsync(IInfiniFrameWindow window, ulong? document, IReadOnlyList<string> registrationMessages) {
        foreach (string registrationMessage in registrationMessages) {
            await SendReadyMessageAsync(window, document, registrationMessage).ConfigureAwait(false);
        }

        await SendReadyMessageAsync(window, document, JsHandlerNames.WindowReadyAck).ConfigureAwait(false);
        return true;
    }

    private static ValueTask SendReadyMessageAsync(IInfiniFrameWindow window, ulong? document, string messageId) {
        // Exact-document delivery checks document retirement; retain the public sender's close-request gate too.
        // A vetoed close may reopen the window, so an interrupted handshake must remain retryable.
        ObjectDisposedException.ThrowIf(window.IsClosedOrClosing(), window.GetType().Name);
        string envelope = InteropEnvelopeProtocol.CreateEnvelopeMessage(messageId);
        return window is IInfiniFrameDocumentWindow documentWindow && document is {} source
            ? documentWindow.SendWebMessageForDocumentAsync(envelope, source, CancellationToken.None)
            : window.SendWebMessageAsync(envelope);
    }
}
