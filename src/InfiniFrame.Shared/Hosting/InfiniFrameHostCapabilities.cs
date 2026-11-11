// ---------------------------------------------------------------------------------------------------------------------
// Code
// ---------------------------------------------------------------------------------------------------------------------
namespace InfiniFrame.Hosting;

/// <summary>
///     Optional operations and notifications a provider implements. Everything not listed here (lifecycle, documents,
///     messages, resources, navigation and the current URL) is mandatory. These are framework capabilities; whether a
///     given application requires one is the application's decision.
/// </summary>
[Flags]
public enum InfiniFrameHostCapabilities : long {
    /// <summary>Only the mandatory contract.</summary>
    None = 0,

    /// <summary>The window host implements <see cref="IInfiniFrameWindowHostBounds" /> (location and size).</summary>
    Bounds = 1L << 0,

    /// <summary>The window host implements <see cref="IInfiniFrameWindowHostState" /> for minimize, maximize, full screen and restore, and honours a non-normal initial state.</summary>
    WindowState = 1L << 1,

    /// <summary>The window host implements <see cref="IInfiniFrameWindowHostState" /> focus requests and reports <see cref="InfiniFrameHostWindowState.Focused" />.</summary>
    Focus = 1L << 2,

    /// <summary>The window host implements <see cref="IInfiniFrameWindowHostTitle" /> (title after creation).</summary>
    Title = 1L << 3,

    /// <summary>The window host implements <see cref="IInfiniFrameWindowHostIcon" /> and honours an initial icon.</summary>
    Icon = 1L << 4,

    /// <summary>
    ///     The provider honours <see cref="InfiniFrameHostWindowRequest.Chromeless" />: a window without title bar,
    ///     borders or other non-client area, whose browser fills the whole window, so the window bounds are the content
    ///     area. Contract 1.1.
    /// </summary>
    Chromeless = 1L << 5,

    /// <summary>
    ///     The provider honours <see cref="InfiniFrameHostWindowRequest.Resizable" /> set to <see langword="false" />:
    ///     the user cannot resize the window, while bounds set through <see cref="IInfiniFrameWindowHostBounds" /> still
    ///     apply. Contract 1.1.
    /// </summary>
    Resizable = 1L << 6,

    /// <summary>
    ///     The provider honours <see cref="InfiniFrameHostWindowRequest.MinimumSize" /> and
    ///     <see cref="InfiniFrameHostWindowRequest.MaximumSize" /> for user resizing. Contract 1.1.
    /// </summary>
    SizeConstraints = 1L << 7,

    /// <summary>The provider raises <see cref="IInfiniFrameWindowHostEvents.BoundsChanged" />.</summary>
    BoundsEvents = 1L << 8,

    /// <summary>The provider raises <see cref="IInfiniFrameWindowHostEvents.StateChanged" /> for minimize, maximize and restore.</summary>
    StateEvents = 1L << 9,

    /// <summary>The provider raises <see cref="IInfiniFrameWindowHostEvents.StateChanged" /> for focus changes.</summary>
    FocusEvents = 1L << 10,

    /// <summary>The provider calls <see cref="IInfiniFrameWindowHostEvents.CloseRequestedAsync" /> before closing natively, so closing can be cancelled.</summary>
    CloseCancellation = 1L << 11
}
