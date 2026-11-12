// ---------------------------------------------------------------------------------------------------------------------
// Imports
// ---------------------------------------------------------------------------------------------------------------------
namespace InfiniFrame.Window.Interop;
// ---------------------------------------------------------------------------------------------------------------------
// Code
// ---------------------------------------------------------------------------------------------------------------------
/// <summary>
///     Holds the registration state for a single window, including the underlying handshake state machine.
/// </summary>
public sealed class WindowRegistrationState {
    // Null retains default windows' per-window handshake; hosted windows identify each document independently.
    internal ulong? Document { get; init; }
    internal WindowRegistrationStateMachine StateMachine { get; } = new();
}
