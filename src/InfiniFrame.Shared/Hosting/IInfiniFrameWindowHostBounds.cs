// ---------------------------------------------------------------------------------------------------------------------
// Imports
// ---------------------------------------------------------------------------------------------------------------------
using System.Drawing;

namespace InfiniFrame.Hosting;
// ---------------------------------------------------------------------------------------------------------------------
// Code
// ---------------------------------------------------------------------------------------------------------------------
/// <summary>Optional window-host operation for <see cref="InfiniFrameHostCapabilities.Bounds" />.</summary>
public interface IInfiniFrameWindowHostBounds {
    /// <summary>The actual native window bounds in screen coordinates.</summary>
    ValueTask<Rectangle> GetBoundsAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Completes once the actual native bounds equal <paramref name="bounds" />, or throws
    ///     <see cref="InfiniFrameHostOperationException" /> when the window system refused or the result is unobservable.
    ///     The size is the outer size (the content area for a chromeless window). When InfiniFrame changes the size, it
    ///     has already clamped it into the window's <see cref="InfiniFrameHostWindowRequest.MinimumSize" /> and
    ///     <see cref="InfiniFrameHostWindowRequest.MaximumSize" />; a move resends the current size unchanged. InfiniFrame
    ///     calls this also for a window that is not <see cref="InfiniFrameHostWindowRequest.Resizable" /> by the user.
    /// </summary>
    ValueTask SetBoundsAsync(Rectangle bounds, CancellationToken cancellationToken);
}
