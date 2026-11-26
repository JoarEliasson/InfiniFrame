using System.Buffers.Binary;
using System.Text;

namespace Contoso.InfiniFrame.CefHost;

/// <summary>Frame kinds of the companion's protocol 3 (the kinds are unchanged from protocol 2).</summary>
internal static class CefKind {
    internal const ushort Hello = 1, Create = 2, Close = 3, Navigate = 4, Send = 5, SetBounds = 7, SetTitle = 8,
        SetState = 9, Shutdown = 10, ResourceResponse = 11, Query = 12, SetIcon = 13;
    internal const ushort Ok = 100, DocumentAttached = 101, Message = 102, ResourceRequest = 103, Closed = 104,
        ViewFailed = 105, Error = 106, ResourceCancelled = 107, DocumentRetired = 108;

    internal static string Name(ushort kind) => kind switch {
        Hello => "hello", Create => "create", Close => "close", Navigate => "navigate", Send => "send",
        SetBounds => "set-bounds", SetTitle => "set-title", SetState => "set-state", Shutdown => "shutdown",
        ResourceResponse => "resource-response", Query => "query", SetIcon => "set-icon",
        Ok => "ok", DocumentAttached => "document-attached", Message => "message", ResourceRequest => "resource-request",
        Closed => "closed", ViewFailed => "view-failed", Error => "error", ResourceCancelled => "resource-cancelled",
        DocumentRetired => "document-retired",
        _ => "unknown"
    };
}

/// <summary>
///     Protocol 3 framing: u32 length, then u16 version, u16 kind, u64 request, view and document generation. Protocol 3
///     adds host options to Hello and window style fields to Create; a peer of any other version is refused.
/// </summary>
internal static class CefProtocol {
    internal const ushort Version = 3;
    internal const int HeaderBytes = 28;
    internal const int MaxFrameBytes = 8 * 1024 * 1024;
    internal const int MaxResourceBytes = MaxFrameBytes - 4096;
    internal const int MaxMimeTypeBytes = 256;
    internal const int MaxQueuedBytes = 64 * 1024 * 1024;
    internal const int MaxQueuedFrames = 256;
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(ushort kind, ulong request, ulong view, ulong generation, Action<BinaryWriter>? payload = null) {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, true);
        writer.Write(0);
        writer.Write(Version);
        writer.Write(kind);
        writer.Write(request);
        writer.Write(view);
        writer.Write(generation);
        payload?.Invoke(writer);
        writer.Flush();
        if (stream.Length - 4 > MaxFrameBytes) throw new InvalidDataException("CEF frame exceeds the 8 MiB limit.");
        byte[] bytes = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)(bytes.Length - 4)));
        return bytes;
    }

    internal static void WriteString(this BinaryWriter writer, string value) {
        int count = Utf8.GetByteCount(value);
        if (count > MaxFrameBytes - HeaderBytes - 4) throw new InvalidDataException("CEF string exceeds the frame limit.");
        writer.Write(count);
        writer.Write(Utf8.GetBytes(value));
    }

    internal static string ReadStringValue(this BinaryReader reader) {
        int length = reader.ReadInt32();
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid CEF string length.");
        return Utf8.GetString(reader.ReadBytes(length));
    }

    internal static async ValueTask<Frame> ReadAsync(Stream stream, CancellationToken cancellationToken) {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (size is < HeaderBytes or > MaxFrameBytes) throw new InvalidDataException("Invalid CEF frame size.");
        byte[] body = new byte[size];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(body);
        if (version != Version)
            throw new InvalidDataException(
                $"CEF protocol version mismatch: the companion sent protocol {version}; this provider implements protocol {Version}.");
        return new Frame(body);
    }

    internal sealed record Frame(byte[] Body) {
        internal ushort Kind => BinaryPrimitives.ReadUInt16LittleEndian(Body.AsSpan(2));
        internal ulong Request => BinaryPrimitives.ReadUInt64LittleEndian(Body.AsSpan(4));
        internal ulong View => BinaryPrimitives.ReadUInt64LittleEndian(Body.AsSpan(12));
        internal ulong Generation => BinaryPrimitives.ReadUInt64LittleEndian(Body.AsSpan(20));
        internal int PayloadBytes => Body.Length - HeaderBytes;
        internal BinaryReader OpenPayload() => new(new MemoryStream(Body, HeaderBytes, Body.Length - HeaderBytes, false), Utf8);
    }

    /// <summary>
    ///     The companion's application-URL rule: <c>app://localhost/</c> only, no user info, port, control characters or
    ///     path traversal. Mirrors <c>AppUrl</c> in the native host.
    /// </summary>
    internal static bool IsApplicationUri(string url) {
        if (url.Length > 65536 || url.AsSpan().IndexOfAny("\r\n\t\\\0") >= 0) return false;
        if (!url.StartsWith(CefHostOptions.Origin, StringComparison.Ordinal)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != "app" || uri.Authority != "localhost" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
            return false;
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        return !path.Contains('\\') && !path.Contains('\0')
            && !path.Split('/').Any(static part => part is ".." || part.Contains(':'));
    }
}

/// <summary>A command the companion rejected (protocol kind 106). The message is the companion's fixed error text.</summary>
internal sealed class CefCommandException(ushort kind, string error) : Exception($"CEF companion rejected '{CefKind.Name(kind)}': {error}") {
    internal const string DocumentRetired = "CEF_DOCUMENT_RETIRED";
    internal ushort CommandKind { get; } = kind;
    internal string Error { get; } = error;
}
