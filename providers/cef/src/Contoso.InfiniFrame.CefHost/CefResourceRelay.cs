using InfiniFrame.Hosting;
using Microsoft.Extensions.Logging;

namespace Contoso.InfiniFrame.CefHost;

/// <summary>
///     Relays one browser resource request to InfiniFrame and its response back to the companion.
/// </summary>
/// <remarks>
///     <para>
///         Status fidelity: InfiniFrame's status, content type and body are passed through unchanged (the companion
///         accepts statuses 200..599 and sets them on the browser response).
///     </para>
///     <para>
///         Containment: a failure that belongs to one request is answered for that request only, and the response
///         (with its body stream) is always disposed. Provider relay errors use 502 (the response could not be
///         relayed: body read failure, a body larger than the 8 MiB frame budget, a content type the companion cannot
///         carry, or a status outside 200..599) and 504 (the provider's resource deadline expired). None of these
///         faults the session. A request the companion cancelled itself gets no response, as the protocol requires.
///         Protocol or provenance corruption is not handled here: it faults the session in the read loop.
///     </para>
/// </remarks>
internal static class CefResourceRelay {
    private const string DefaultMime = "text/plain; charset=utf-8";
    private const int ChunkBytes = 64 * 1024;

    internal static async Task ServeAsync(CefHost host, CefWindowHost? window, IInfiniFrameWindowHostEvents? events,
        CefProtocol.Frame frame, CefResourceLease lease, ILogger logger) {
        string method, url;
        using (BinaryReader reader = frame.OpenPayload()) {
            method = reader.ReadStringValue();
            url = reader.ReadStringValue();
        }
        Answer? answer;
        if (window is null || events is null || window.IsClosedNatively) answer = new Answer(503, DefaultMime, []);
        else if (!CefProtocol.IsApplicationUri(url)) answer = new Answer(403, DefaultMime, []);
        else answer = await RelayAsync(host, events, method, url, lease, logger).ConfigureAwait(false);
        if (answer is null || host.Connection.IsFaulted) return;
        try {
            host.Connection.Send(CefKind.ResourceResponse, frame.Request, frame.View, frame.Generation, writer => {
                writer.Write(answer.Status);
                writer.WriteString(answer.Mime);
                writer.Write(answer.Body.Length);
                writer.Write(answer.Body);
            });
            host.Diagnostics.ResourceAnswered(answer.Status, $"{method} {SafePath(url)}");
        }
        catch (Exception exception) when (exception is InfiniFrameHostOperationException or ObjectDisposedException) {
            // The session ended (or overflowed and faulted) while this request was in flight.
            logger.LogDebug(exception, "A CEF resource response could not be sent");
        }
    }

    private static async Task<Answer?> RelayAsync(CefHost host, IInfiniFrameWindowHostEvents events, string method, string url,
        CefResourceLease lease, ILogger logger) {
        InfiniFrameHostResourceResponse? response = null;
        try {
            response = await events.GetResourceAsync(new InfiniFrameHostResourceRequest { Method = method, Url = new Uri(url) }, lease.Token)
                .ConfigureAwait(false);
            if (response.StatusCode is < 200 or > 599) return Error(host, "status-out-of-range", 502);
            string mime = response.ContentType ?? DefaultMime;
            if (response.Body is not {} body) return new Answer(response.StatusCode, IsCarriableMime(mime) ? mime : DefaultMime, []);
            if (!IsCarriableMime(mime)) return Error(host, "invalid-mime", 502);
            if (response.ContentLength > CefProtocol.MaxResourceBytes) return Error(host, "oversize", 502);
            byte[]? bytes = await ReadBoundedAsync(body, lease.Token).ConfigureAwait(false);
            return bytes is null ? Error(host, "oversize", 502) : new Answer(response.StatusCode, mime, bytes);
        }
        catch (OperationCanceledException) when (lease.Token.IsCancellationRequested) {
            if (lease.CancelledByCompanion || host.Connection.IsFaulted) return null;
            return Error(host, "timeout", 504);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
            // Never log the URL query or any body; the path is enough to locate the failing asset.
            logger.LogWarning("A CEF resource request for {Path} failed in the relay ({Type}); answered with 502", SafePath(url), exception.GetType().Name);
            return Error(host, exception is IOException ? "read-failure" : "relay-failure", 502);
        }
        finally {
            if (response is not null) {
                try { await response.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
                    logger.LogDebug(exception, "Disposing a CEF resource response failed");
                }
            }
        }
    }

    /// <summary>Reads at most <see cref="CefProtocol.MaxResourceBytes" />; returns null when the body is larger.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream body, CancellationToken cancellationToken) {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[ChunkBytes];
        while (true) {
            int count = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0) return buffer.ToArray();
            if (buffer.Length + count > CefProtocol.MaxResourceBytes) return null;
            buffer.Write(chunk, 0, count);
        }
    }

    /// <summary>The companion's MIME rule (non-empty, at most 256 bytes, no CR/LF/NUL), restricted to printable ASCII.</summary>
    internal static bool IsCarriableMime(string mime) =>
        mime.Length is > 0 and <= CefProtocol.MaxMimeTypeBytes && mime.All(static character => character is >= ' ' and <= '~');

    private static string SafePath(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.AbsolutePath : "(invalid)";

    private static Answer Error(CefHost host, string reason, int status) {
        host.Diagnostics.ResourceRelayError(reason);
        return new Answer(status, DefaultMime, []);
    }

    private sealed record Answer(int Status, string Mime, byte[] Body);
}
