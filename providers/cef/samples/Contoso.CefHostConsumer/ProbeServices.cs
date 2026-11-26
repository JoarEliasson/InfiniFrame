using System.Collections.Concurrent;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Contoso.CefHostConsumer;

/// <summary>Application-lifetime probe state (singleton): one identity for the whole run.</summary>
public sealed class ProbeState {
    public Guid Identity { get; } = Guid.NewGuid();
    public int NativeEvents;
    public long Ticks;
    public int ComponentsOpened;
    public int ComponentsDisposed;
    public int SecondaryMessages;
    public int SecondaryClosed;
    public int CrossWindowMessages;
    public int CallbackNavigations;
    public int CallbackNavigationsCompleted;
    public int DelayedMilliseconds = 1500;
    public int DelayedRenderAttempts;
    public int DelayedRenderCompleted;
    public int DelayedJsAttempts;
    public int DelayedJsCompleted;
    public int DelayedDispatcherJsAttempts;
    public int DelayedNavigationAttempts;
    public readonly ConcurrentQueue<string> DelayedOutcomes = new();
    public readonly ConcurrentDictionary<string, int> AssetRequests = new();
    public readonly ConcurrentDictionary<int, int> HandlerThreads = new();
    public int HandlerSawSynchronizationContext;
}

/// <summary>
///     An application-lifetime producer (singleton) started by the first page. Its <see cref="Timer" /> keeps the
///     execution context of the page that created it, exactly like real application timers do. Every page component
///     subscribes; after a reload, its ticks must keep updating the current page.
/// </summary>
public sealed class LifetimeProducer : IDisposable {
    private Timer? _timer;
    private long _ticks;
    private int _startedByPage;

    public event Action<long>? Ticked;
    public int StartedByPage => Volatile.Read(ref _startedByPage);
    public long TickCount => Interlocked.Read(ref _ticks);

    public void EnsureStarted(int page) {
        if (Interlocked.CompareExchange(ref _startedByPage, page, 0) != 0) return;
        _timer = new Timer(_ => Ticked?.Invoke(Interlocked.Increment(ref _ticks)), null, 250, 250);
    }

    public void Dispose() => _timer?.Dispose();
}

/// <summary>
///     Wraps Blazor's static file provider to inject resource failures through the real request path:
///     <c>__fault/throws.css</c> (the asset store throws), <c>__fault/read-error.bin</c> (the body fails mid-read),
///     <c>__fault/oversize.bin</c> (9 MiB, seekable) and <c>__fault/oversize-stream.bin</c> (9 MiB, not seekable).
/// </summary>
internal sealed class FaultFileProvider(IFileProvider inner, ProbeState state) : IFileProvider {
    internal const int OversizeBytes = 9 * 1024 * 1024;

    public IFileInfo GetFileInfo(string subpath) {
        string path = subpath.TrimStart('/');
        state.AssetRequests.AddOrUpdate(path, 1, static (_, count) => count + 1);
        return path switch {
            "__fault/throws.css" => throw new IOException("Injected asset-store failure."),
            "__fault/read-error.bin" => new FaultFile("read-error.bin", -1, static () => new FailingStream(4096)),
            "__fault/oversize.bin" => new FaultFile("oversize.bin", OversizeBytes, static () => new SizedStream(OversizeBytes, seekable: true)),
            "__fault/oversize-stream.bin" => new FaultFile("oversize-stream.bin", -1, static () => new SizedStream(OversizeBytes, seekable: false)),
            _ => inner.GetFileInfo(subpath)
        };
    }

    public IDirectoryContents GetDirectoryContents(string subpath) => inner.GetDirectoryContents(subpath);
    public IChangeToken Watch(string filter) => inner.Watch(filter);

    private sealed class FaultFile(string name, long length, Func<Stream> open) : IFileInfo {
        public bool Exists => true;
        public long Length => length;
        public string? PhysicalPath => null;
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public bool IsDirectory => false;
        public Stream CreateReadStream() => open();
    }

    /// <summary>Returns some bytes, then fails like a storage read error.</summary>
    private sealed class FailingStream(int okBytes) : Stream {
        private int _served;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() {}
        public override int Read(byte[] buffer, int offset, int count) {
            if (_served >= okBytes) throw new IOException("Injected storage read failure mid-body.");
            int n = Math.Min(count, okBytes - _served);
            Array.Clear(buffer, offset, n);
            _served += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SizedStream(long size, bool seekable) : Stream {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => seekable ? size : throw new NotSupportedException();
        public override long Position {
            get => seekable ? _position : throw new NotSupportedException();
            set => _position = seekable ? value : throw new NotSupportedException();
        }
        public override void Flush() {}
        public override int Read(byte[] buffer, int offset, int count) {
            int n = (int)Math.Min(count, size - _position);
            if (n <= 0) return 0;
            Array.Fill(buffer, (byte)'x', offset, n);
            _position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
