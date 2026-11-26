using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace LocalControlsUi.Services;

public sealed record ControlSnapshot(int Value, long Heartbeat, int Temperature, int Flow, int Load);

/// <summary>RCL-owned counters only. These do not measure host dispatchers, DI scopes or native processes.</summary>
public sealed record ControlDiagnostics(int ActiveBackends, int ActiveSessions, long CreatedSessions,
    long RetiredSessions, int ActiveSubscriptions, long CreatedSubscriptions, long RetiredSubscriptions);

/// <summary>One instance per application provider. The value survives document replacement in that provider.</summary>
public sealed class FakeControlBackend : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<long, Dictionary<long, Channel<ControlSnapshot>>> _sessions = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _heartbeatTask;
    private int _value = 50;
    private long _heartbeat;
    private long _createdSessions;
    private long _retiredSessions;
    private long _createdSubscriptions;
    private long _retiredSubscriptions;
    private bool _disposed;
    private int _stopDisposed;

    public FakeControlBackend() => _heartbeatTask = RunHeartbeatAsync();

    public ControlSnapshot Read()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Snapshot();
        }
    }

    public ControlDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return new(_disposed ? 0 : 1, _sessions.Count, _createdSessions, _retiredSessions,
                    _sessions.Values.Sum(s => s.Count), _createdSubscriptions, _retiredSubscriptions);
            }
        }
    }

    public DocumentSession OpenSession()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var id = ++_createdSessions;
            _sessions.Add(id, []);
            return new(this, id);
        }
    }

    internal void Write(long sessionId, int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
        lock (_gate)
        {
            RequireSession(sessionId);
            _value = value;
            Publish();
        }
    }

    internal ControlSubscription Subscribe(long sessionId)
    {
        lock (_gate)
        {
            RequireSession(sessionId);
            var channel = Channel.CreateBounded<ControlSnapshot>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            var id = ++_createdSubscriptions;
            _sessions[sessionId].Add(id, channel);
            channel.Writer.TryWrite(Snapshot());
            return new(this, sessionId, id, channel.Reader);
        }
    }

    internal void RetireSubscription(long sessionId, long subscriptionId)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(sessionId, out var subscriptions) &&
                subscriptions.Remove(subscriptionId, out var channel))
            {
                channel.Writer.TryComplete();
                _retiredSubscriptions++;
            }
        }
    }

    internal void RetireSession(long sessionId)
    {
        lock (_gate)
        {
            if (!_sessions.Remove(sessionId, out var subscriptions)) return;
            foreach (var channel in subscriptions.Values) channel.Writer.TryComplete();
            _retiredSubscriptions += subscriptions.Count;
            _retiredSessions++;
        }
    }

    private void RequireSession(long sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed || !_sessions.ContainsKey(sessionId), typeof(DocumentSession));
    }

    private ControlSnapshot Snapshot() => new(_value, _heartbeat,
        39 + (int)(_heartbeat % 9), 55 + (int)((_heartbeat / 2) % 16), 58 + (int)((_heartbeat / 3) % 15));

    private void Publish()
    {
        var snapshot = Snapshot();
        foreach (var subscriptions in _sessions.Values)
            foreach (var channel in subscriptions.Values) channel.Writer.TryWrite(snapshot);
    }

    private async Task RunHeartbeatAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _heartbeat++;
                    Publish();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                foreach (var subscriptions in _sessions.Values)
                    foreach (var channel in subscriptions.Values) channel.Writer.TryComplete();
                _retiredSubscriptions = _createdSubscriptions;
                _retiredSessions = _createdSessions;
                _sessions.Clear();
                _stop.Cancel();
            }
        }
        // All callers await the same worker, including repeated disposal.
        await _heartbeatTask.ConfigureAwait(false);
        if (Interlocked.Exchange(ref _stopDisposed, 1) == 0) _stop.Dispose();
    }
}

/// <summary>Explicit App-owned document lifetime, independent of the host's DI scope policy.</summary>
public sealed class DocumentSession : IDisposable
{
    private readonly FakeControlBackend _backend;
    internal DocumentSession(FakeControlBackend backend, long id) => (_backend, Id) = (backend, id);
    public long Id { get; }
    public void WriteValue(int value) => _backend.Write(Id, value);
    public ControlSubscription Subscribe() => _backend.Subscribe(Id);
    public void Dispose() => _backend.RetireSession(Id);
}

/// <summary>Bounded latest-value stream. The backend never stores a component delegate.</summary>
public sealed class ControlSubscription : IDisposable
{
    private readonly FakeControlBackend _backend;
    private readonly long _sessionId;
    private readonly long _id;
    private readonly ChannelReader<ControlSnapshot> _reader;

    internal ControlSubscription(FakeControlBackend backend, long sessionId, long id, ChannelReader<ControlSnapshot> reader)
        => (_backend, _sessionId, _id, _reader) = (backend, sessionId, id, reader);

    public async IAsyncEnumerable<ControlSnapshot> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var snapshot in _reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return snapshot;
    }

    public void Dispose() => _backend.RetireSubscription(_sessionId, _id);
}
