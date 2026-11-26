using System.Drawing;
using InfiniFrame.Hosting;
using Microsoft.Extensions.Logging;

namespace Contoso.InfiniFrame.CefHost;

/// <summary>
///     One companion view (a CEF Views window with one browser) behind InfiniFrame's window facade.
/// </summary>
/// <remarks>
///     Document identity: the companion's native document generations are opaque 64-bit values (a random seed mixed
///     with a counter), not ordered. InfiniFrame requires non-zero, strictly increasing ids per window, so this class
///     assigns public ids 1, 2, 3… as attach frames are read, in frame order, and keeps the mapping to native
///     generations. Messages are accepted only from the current native generation; sends go only to it.
/// </remarks>
internal sealed class CefWindowHost : IInfiniFrameWindowHost, IInfiniFrameWindowHostBounds, IInfiniFrameWindowHostState,
    IInfiniFrameWindowHostTitle, IInfiniFrameWindowHostIcon {
    internal const int MaxTitleBytes = 4096;
    private const int RememberedGenerations = 16;
    private readonly CefHost _host;
    private readonly IInfiniFrameWindowHostEvents _events;
    private readonly ILogger _logger;
    private readonly object _documents = new();
    private readonly Dictionary<ulong, ulong> _known = [];
    private readonly Queue<ulong> _knownOrder = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ulong _nextDocument;
    private ulong _currentGeneration;
    private ulong _currentDocument;
    private int _closedNatively;
    private int _closeSent;
    private int _detachedFromFacade;
    private int _disposed;

    internal CefWindowHost(CefHost host, ulong view, IInfiniFrameWindowHostEvents events, ILogger logger) {
        _host = host;
        View = view;
        _events = events;
        _logger = logger;
    }

    internal ulong View { get; }
    internal IInfiniFrameWindowHostEvents Events => _events;
    internal Task NativeClosed => _closed.Task;
    internal bool IsClosedNatively => Volatile.Read(ref _closedNatively) != 0;
    private CefConnection Connection => _host.Connection;

    // -----------------------------------------------------------------------------------------------------------------
    // Frames (read loop, frame order). Returns the sink work for the ordered event loop.
    // -----------------------------------------------------------------------------------------------------------------
    internal Action? OnFrameRead(CefProtocol.Frame frame) {
        using BinaryReader reader = frame.OpenPayload();
        bool deliver = Volatile.Read(ref _detachedFromFacade) == 0;
        switch (frame.Kind) {
            case CefKind.DocumentAttached: {
                string url = reader.ReadStringValue();
                if (frame.Generation == 0 || !CefProtocol.IsApplicationUri(url))
                    throw new InvalidDataException("The CEF companion supplied invalid document provenance.");
                ulong id;
                lock (_documents) {
                    if (_known.ContainsKey(frame.Generation))
                        throw new InvalidDataException("The CEF companion attached a native document generation twice.");
                    id = ++_nextDocument;
                    Remember(frame.Generation, id);
                    _currentGeneration = frame.Generation;
                    _currentDocument = id;
                }
                _host.Diagnostics.DocumentAttached(View, id);
                var uri = new Uri(url);
                return deliver ? () => _events.DocumentAttached(new InfiniFrameDocumentId(id), uri) : null;
            }
            case CefKind.Message: {
                string text = reader.ReadStringValue();
                ulong id;
                lock (_documents) {
                    if (frame.Generation == 0 || frame.Generation != _currentGeneration) {
                        _host.Diagnostics.StaleMessageDropped();
                        return null;
                    }
                    id = _currentDocument;
                }
                _host.Diagnostics.MessageType("in", text);
                _host.Diagnostics.ReceivedFrom(View, id);
                return deliver ? () => _events.MessageReceived(new InfiniFrameDocumentId(id), text) : null;
            }
            case CefKind.DocumentRetired: {
                ulong id;
                lock (_documents) {
                    if (frame.Generation == 0 || frame.Generation != _currentGeneration) return null;
                    id = _currentDocument;
                    _currentGeneration = 0;
                    _currentDocument = 0;
                }
                _host.Diagnostics.DocumentRetired(View, id);
                return deliver ? () => _events.DocumentRetired(new InfiniFrameDocumentId(id)) : null;
            }
            case CefKind.Closed: {
                Volatile.Write(ref _closedNatively, 1);
                lock (_documents) {
                    _currentGeneration = 0;
                    _currentDocument = 0;
                }
                _host.Diagnostics.ViewClosed();
                _host.ForgetView(this);
                return () => {
                    if (deliver) _events.Closed();
                    _closed.TrySetResult();
                };
            }
            case CefKind.ViewFailed: {
                string reason = reader.ReadStringValue();
                _host.Diagnostics.ViewFault(reason);
                var fault = new InfiniFrameHostOperationException($"The CEF view failed: {reason}.");
                return deliver ? () => _events.Faulted(fault) : null;
            }
            default:
                throw new InvalidDataException($"Unexpected CEF view event kind {frame.Kind}.");
        }
    }

    private void Remember(ulong generation, ulong id) {
        _known[generation] = id;
        _knownOrder.Enqueue(generation);
        while (_knownOrder.Count > RememberedGenerations) _known.Remove(_knownOrder.Dequeue());
    }

    /// <summary>The facade gave up on this window (creation abandoned): stop delivering its events to InfiniFrame.</summary>
    internal void DetachFromFacade() => Volatile.Write(ref _detachedFromFacade, 1);

    /// <summary>Closes a native window nobody owns any more (a late creation after its caller gave up).</summary>
    internal async Task ReclaimAsync() {
        DetachFromFacade();
        _host.Diagnostics.ViewReclaimed();
        _logger.LogWarning("Reclaiming CEF view {View}: its native creation finished after the caller gave up", View);
        await RequestCloseAsync(CancellationToken.None).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // IInfiniFrameWindowHost
    // -----------------------------------------------------------------------------------------------------------------
    /// <summary>
    ///     Completes once the close command is queued for the companion; closure itself is reported by Closed. Repeated
    ///     calls are harmless. The command's reply is still observed (a failure is reported as a late fault).
    /// </summary>
    public async ValueTask RequestCloseAsync(CancellationToken cancellationToken) {
        if (IsClosedNatively || Volatile.Read(ref _closeSent) != 0) return;
        await Connection.SendDetachedAsync(CefKind.Close, View, 0, null, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _closeSent, 1);
    }

    public async ValueTask<InfiniFrameHostNavigation> NavigateAsync(Uri url, CancellationToken cancellationToken) {
        string target = url.AbsoluteUri;
        if (!CefProtocol.IsApplicationUri(target))
            throw new InfiniFrameHostOperationException("The CEF companion navigates only within app://localhost/.");
        CefProtocol.Frame reply = await CommandAsync(CefKind.Navigate, 0, writer => writer.WriteString(target), cancellationToken).ConfigureAwait(false);
        using BinaryReader reader = reply.OpenPayload();
        if (reader.BaseStream.Length - reader.BaseStream.Position != 1) throw new InvalidDataException("Invalid CEF navigation result length.");
        byte sameDocument = reader.ReadByte();
        if (sameDocument > 1 || reply.Generation == 0) throw new InvalidDataException("Invalid CEF navigation result.");
        lock (_documents) {
            if (sameDocument == 1) {
                // A fragment change keeps its document; report the document now current for that generation.
                if (_known.TryGetValue(reply.Generation, out ulong same)) return new InfiniFrameHostNavigation(new InfiniFrameDocumentId(same), true);
                throw new InfiniFrameHostOperationException("The CEF navigation result references an unknown document.");
            }
            // A document navigation succeeds natively only after its attach frame, which was read (in order) before this reply.
            if (_known.TryGetValue(reply.Generation, out ulong attached)) return new InfiniFrameHostNavigation(new InfiniFrameDocumentId(attached), false);
        }
        throw new InfiniFrameHostOperationException("The CEF navigation result references a document that never attached.");
    }

    public async ValueTask SendMessageAsync(InfiniFrameDocumentId document, string message, CancellationToken cancellationToken) {
        ulong generation;
        lock (_documents) {
            if (document.Value == 0 || document.Value != _currentDocument) throw new InfiniFrameHostDocumentRetiredException(document);
            generation = _currentGeneration;
        }
        try {
            await Connection.RequestAsync(CefKind.Send, View, generation, writer => writer.WriteString(message), cancellationToken).ConfigureAwait(false);
            _host.Diagnostics.MessageType("out", message);
            _host.Diagnostics.SentTo(View, document.Value);
        }
        catch (CefCommandException exception) when (exception.Error == CefCommandException.DocumentRetired) {
            throw new InfiniFrameHostDocumentRetiredException(document);
        }
        catch (CefCommandException exception) {
            // The active document refused or failed the message: observable, never silently dropped.
            _host.Diagnostics.MessageType("out-failed", message);
            throw new InfiniFrameHostOperationException(
                $"The CEF companion could not deliver a message to active document {document.Value}: {exception.Error}.", exception);
        }
        catch (InvalidDataException exception) {
            // The message cannot be framed (larger than the 8 MiB frame budget): it was not sent anywhere.
            _host.Diagnostics.MessageType("out-failed", message);
            throw new InfiniFrameHostOperationException(
                $"A message for active document {document.Value} exceeds the companion's 8 MiB frame budget and was not delivered.", exception);
        }
    }

    public async ValueTask<Uri?> GetCurrentUrlAsync(CancellationToken cancellationToken) {
        string url = (await QueryAsync(cancellationToken).ConfigureAwait(false)).Url;
        return url.Length == 0 ? null : new Uri(url);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Optional operations (Bounds, WindowState + Focus, Title, Icon)
    // -----------------------------------------------------------------------------------------------------------------
    public async ValueTask<Rectangle> GetBoundsAsync(CancellationToken cancellationToken) =>
        (await QueryAsync(cancellationToken).ConfigureAwait(false)).Bounds;

    public async ValueTask SetBoundsAsync(Rectangle bounds, CancellationToken cancellationToken) {
        if (!SupportedBounds(bounds))
            throw new InfiniFrameHostOperationException(
                $"The CEF companion supports positions within ±100000 and sizes 64..16384; {bounds} is outside that range.");
        await CommandAsync(CefKind.SetBounds, 0, writer => {
            writer.Write(bounds.X);
            writer.Write(bounds.Y);
            writer.Write(bounds.Width);
            writer.Write(bounds.Height);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static bool SupportedBounds(Rectangle bounds) =>
        bounds.X is >= -100000 and <= 100000 && bounds.Y is >= -100000 and <= 100000
        && bounds.Width is >= 64 and <= 16384 && bounds.Height is >= 64 and <= 16384;

    public async ValueTask<InfiniFrameHostWindowState> GetStateAsync(CancellationToken cancellationToken) {
        int flags = (await QueryAsync(cancellationToken).ConfigureAwait(false)).Flags;
        InfiniFrameHostWindowState state = InfiniFrameHostWindowState.Normal;
        if ((flags & 1) != 0) state |= InfiniFrameHostWindowState.FullScreen;
        if ((flags & 2) != 0) state |= InfiniFrameHostWindowState.Minimized;
        if ((flags & 4) != 0) state |= InfiniFrameHostWindowState.Maximized;
        if ((flags & 8) != 0) state |= InfiniFrameHostWindowState.Focused;
        return state;
    }

    public async ValueTask SetStateAsync(InfiniFrameHostWindowStateChange change, CancellationToken cancellationToken) {
        int native = change switch {
            InfiniFrameHostWindowStateChange.Focus => 1,
            InfiniFrameHostWindowStateChange.EnterFullScreen => 2,
            InfiniFrameHostWindowStateChange.ExitFullScreen => 3,
            InfiniFrameHostWindowStateChange.Minimize => 4,
            InfiniFrameHostWindowStateChange.Maximize => 5,
            InfiniFrameHostWindowStateChange.Restore => 6,
            _ => throw new InfiniFrameHostOperationException($"Unsupported window state change {change}.")
        };
        await CommandAsync(CefKind.SetState, 0, writer => writer.Write(native), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string> GetTitleAsync(CancellationToken cancellationToken) =>
        (await QueryAsync(cancellationToken).ConfigureAwait(false)).Title;

    public async ValueTask SetTitleAsync(string title, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(title);
        if (CefProtocol.Utf8.GetByteCount(title) > MaxTitleBytes)
            throw new InfiniFrameHostOperationException($"CEF window titles are limited to {MaxTitleBytes} UTF-8 bytes.");
        await CommandAsync(CefKind.SetTitle, 0, writer => writer.WriteString(title), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetIconAsync(ReadOnlyMemory<byte> png, CancellationToken cancellationToken) {
        byte[] copy = png.ToArray();
        if (ValidateIcon(copy) is {} problem) throw new InfiniFrameHostOperationException(problem);
        await CommandAsync(CefKind.SetIcon, 0, writer => {
            writer.Write(copy.Length);
            writer.Write(copy);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The companion's PNG rules (signature, at most 1 MiB, 1..512 pixels per side); null when acceptable.</summary>
    internal static string? ValidateIcon(ReadOnlySpan<byte> png) {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < 24 || png.Length > 1024 * 1024 || !png[..8].SequenceEqual(signature))
            return "CEF window icons must be PNG files of at most 1 MiB.";
        uint width = (uint)(png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19]);
        uint height = (uint)(png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23]);
        return width is >= 1 and <= 512 && height is >= 1 and <= 512 ? null : "CEF window icon dimensions must be 1..512 pixels.";
    }

    private async Task<(Rectangle Bounds, int Flags, string Title, string Url)> QueryAsync(CancellationToken cancellationToken) {
        CefProtocol.Frame reply = await CommandAsync(CefKind.Query, 0, null, cancellationToken).ConfigureAwait(false);
        using BinaryReader reader = reply.OpenPayload();
        var bounds = new Rectangle(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        int flags = reader.ReadInt32();
        string title = reader.ReadStringValue();
        string url = reader.ReadStringValue();
        if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Invalid CEF window state query result.");
        return (bounds, flags, title, url);
    }

    /// <summary>A command on this view; a companion rejection becomes <see cref="InfiniFrameHostOperationException" />.</summary>
    private async Task<CefProtocol.Frame> CommandAsync(ushort kind, ulong generation, Action<BinaryWriter>? payload, CancellationToken cancellationToken) {
        try {
            return await Connection.RequestAsync(kind, View, generation, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (CefCommandException exception) {
            throw new InfiniFrameHostOperationException($"The CEF companion rejected '{CefKind.Name(kind)}': {exception.Error}.", exception);
        }
    }

    /// <summary>
    ///     Disposal releases provider state only. InfiniFrame disposes the window host after its terminal notification;
    ///     if the native view is somehow still open, it is closed (detached) so nothing is abandoned.
    /// </summary>
    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        DetachFromFacade();
        if (IsClosedNatively || Volatile.Read(ref _closeSent) != 0 || Connection.IsFaulted) return;
        try { await RequestCloseAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
            _logger.LogDebug(exception, "Closing CEF view {View} during disposal failed", View);
        }
    }
}
