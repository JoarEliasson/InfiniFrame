using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using InfiniFrame.Hosting;
using Microsoft.Extensions.Logging;

namespace Contoso.InfiniFrame.CefHost;

/// <summary>How the companion process is started: the payload host in production, a scripted companion in tests.</summary>
internal sealed record CefLaunch(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>Receives the session's frames. Implemented by the host.</summary>
internal interface ICefFrameRouter {
    /// <summary>
    ///     Runs on the read loop, in frame order, for every view event (101, 102, 104, 105, 108). Must be quick and never
    ///     block. Returns the work to run on the ordered event loop, or <see langword="null" />. Throws
    ///     <see cref="InvalidDataException" /> for a protocol violation, which faults the session.
    /// </summary>
    Action? OnEvent(CefProtocol.Frame frame);

    /// <summary>Serves one resource request (103) on the resource worker.</summary>
    Task ServeResourceAsync(CefProtocol.Frame frame, CefResourceLease lease);

    /// <summary>A command whose caller stopped waiting (or that was sent detached) finished: reply or error.</summary>
    void OnLateCompletion(CefPendingCommand command, CefProtocol.Frame? reply, Exception? error);
}

/// <summary>One live resource request: its cancellation and why it was cancelled.</summary>
internal sealed class CefResourceLease(CancellationTokenSource cancellation) {
    private int _cancelledByCompanion;
    internal CancellationTokenSource Source { get; } = cancellation;
    internal CancellationToken Token => Source.Token;
    /// <summary>The companion finished this request itself (kind 107); no response may follow.</summary>
    internal bool CancelledByCompanion => Volatile.Read(ref _cancelledByCompanion) != 0;
    internal void MarkCancelledByCompanion() => Volatile.Write(ref _cancelledByCompanion, 1);
}

/// <summary>One outstanding companion command.</summary>
internal sealed class CefPendingCommand(ushort kind, ulong request, ulong view, string reason) {
    private int _state;// 0 waiting, 1 abandoned (nobody waits), 2 finished
    internal ushort Kind { get; } = kind;
    internal ulong Request { get; } = request;
    internal ulong View { get; } = view;
    internal long StartedTimestamp { get; } = Stopwatch.GetTimestamp();
    internal string Reason { get; private set; } = reason;
    internal Action<CefProtocol.Frame?, Exception?>? OnLate { get; init; }
    internal TaskCompletionSource<CefProtocol.Frame> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Abandoned => Volatile.Read(ref _state) == 1;

    internal bool TryAbandon(string why) {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return false;
        Reason = why;
        return true;
    }

    /// <summary>Returns the state before finishing: 0 = a caller is waiting, 1 = abandoned, 2 = already finished.</summary>
    internal int Finish() => Interlocked.Exchange(ref _state, 2);
}

/// <summary>
///     One companion process and its protocol-3 session over inherited stdin/stdout pipes.
/// </summary>
/// <remarks>
///     <para>
///         Threads: the read, write, event, resource, stderr and exit loops are thread-pool tasks started with the
///         caller's <see cref="ExecutionContext" /> suppressed, so no application or InfiniFrame <c>AsyncLocal</c> state
///         flows into them. Replies are matched on the read loop. View events are pre-processed on the read loop in
///         frame order (document identity), then delivered in that order by the single event loop. Resource requests
///         are served by one resource worker.
///     </para>
///     <para>
///         Commands: at most 128 are outstanding. A command whose caller stops waiting is abandoned, not forgotten: its
///         slot stays taken until the companion answers, and a late reply or late error is reported. A command left
///         unanswered for <see cref="CefHostOptions.CommandTimeout" /> faults the session, because its delivery is
///         uncertain and it is never replayed.
///     </para>
/// </remarks>
internal sealed class CefConnection : IAsyncDisposable {
    internal const int MaxOutstandingCommands = 128;
    internal const int MaxActiveResources = 16;
    private readonly Process _process;
    private readonly CefProcessTree _tree;
    private readonly ILogger _logger;
    private readonly CefHostOptions _options;
    private readonly CefDiagnosticsCollector _diagnostics;
    private readonly ICefFrameRouter _router;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(CefProtocol.MaxQueuedFrames) { SingleReader = true });
    private readonly Channel<QueuedEvent> _events = Channel.CreateBounded<QueuedEvent>(new BoundedChannelOptions(CefProtocol.MaxQueuedFrames) { SingleReader = true, SingleWriter = true });
    private readonly Channel<ResourceWork> _resources = Channel.CreateBounded<ResourceWork>(new BoundedChannelOptions(MaxActiveResources) { SingleReader = true, SingleWriter = true });
    private readonly ConcurrentDictionary<ulong, CefPendingCommand> _pending = new();
    private readonly SemaphoreSlim _slots = new(MaxOutstandingCommands, MaxOutstandingCommands);
    private readonly object _resourceGate = new();
    private readonly Dictionary<ulong, CefResourceLease> _activeResources = [];
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _exitReported = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _stderrTask;
    private readonly Task[] _workers;
    private long _request;
    private long _queuedOutputBytes;
    private long _queuedInputBytes;
    private long _peakQueuedBytes;
    private int _disposing;
    private int _expectedShutdown;
    private int _escalating;
    private Exception? _failure;

    internal CefConnection(CefLaunch launch, CefHostOptions options, ILogger logger, CefDiagnosticsCollector diagnostics, ICefFrameRouter router) {
        _options = options;
        _logger = logger;
        _diagnostics = diagnostics;
        _router = router;
        var start = new ProcessStartInfo {
            FileName = launch.FileName,
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in launch.Arguments) start.ArgumentList.Add(argument);
        // The companion is not a build harness: credentials used by repository tooling never reach it.
        foreach (string key in start.Environment.Keys.Where(static key =>
                     key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                     || key.Contains("SECRET", StringComparison.OrdinalIgnoreCase)
                     || key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        foreach ((string key, string value) in launch.Environment) start.Environment[key] = value;
        _process = Process.Start(start) ?? throw new InfiniFrameHostOperationException("Could not launch the CEF companion.");
        ProcessId = _process.Id;
        try { ProcessStartTime = _process.StartTime; }
        catch (InvalidOperationException) {}
        _diagnostics.CompanionStarted(ProcessId);
        // Before the hello handshake: the companion starts no process until then, so the job holds its whole tree.
        _tree = CefProcessTree.Attach(_process, logger);
        _diagnostics.ProcessTracking(_tree.Tracking);
        // Long-lived loops must not capture whoever happened to start the host (application or InfiniFrame state).
        using (ExecutionContext.SuppressFlow()) {
            _stderrTask = Task.Run(DrainErrors);
            _workers = [Task.Run(ReadLoop), Task.Run(WriteLoop), Task.Run(EventLoop), Task.Run(ResourceLoop), _stderrTask, Task.Run(ObserveExit)];
        }
    }

    internal int ProcessId { get; }
    internal CefProcessTree Tree => _tree;
    internal DateTime? ProcessStartTime { get; }
    internal Task Completion => _completion.Task;
    internal bool IsFaulted => Volatile.Read(ref _failure) is not null;
    internal int? ExitCode => _process.HasExited ? _process.ExitCode : null;
    internal CefStderrTail Stderr { get; } = new();
    internal long QueuedBytes => Interlocked.Read(ref _queuedOutputBytes) + Interlocked.Read(ref _queuedInputBytes);
    internal long PeakQueuedBytes => Interlocked.Read(ref _peakQueuedBytes);
    internal int PendingCommands => _pending.Count;
    internal int AbandonedCommands => _pending.Values.Count(static command => command.Abandoned);

    internal async Task HandshakeAsync(CancellationToken cancellationToken) {
        byte[] nonce = RandomNumberGenerator.GetBytes(32);
        uint hostOptions = _options.HostOptionFlags;
        CefProtocol.Frame reply = await RequestAsync(CefKind.Hello, 0, 0, writer => {
            writer.Write(nonce);
            writer.WriteString(CefHostOptions.Origin);
            // Protocol 3 host options: flags, then the forced device scale factor (0: the platform's).
            writer.Write(hostOptions);
            writer.Write(_options.DeviceScaleFactor ?? 0d);
        }, cancellationToken).ConfigureAwait(false);
        using BinaryReader reader = reply.OpenPayload();
        if (!CryptographicOperations.FixedTimeEquals(nonce, reader.ReadBytes(32))
            || reader.ReadStringValue() != CefHostOptions.CurrentRid
            || reader.ReadStringValue() != CefHostOptions.CefVersion) {
            var mismatch = new InvalidDataException("CEF companion authentication, architecture or version mismatch.");
            Fail(mismatch);
            throw mismatch;
        }
        if (reader.BaseStream.Length - reader.BaseStream.Position != 4 || reader.ReadUInt32() != hostOptions) {
            var mismatch = new InvalidDataException("The CEF companion did not confirm the requested host options.");
            Fail(mismatch);
            throw mismatch;
        }
    }

    /// <summary>Sends one command and waits for its reply. See the class remarks for abandonment and timeouts.</summary>
    internal async Task<CefProtocol.Frame> RequestAsync(ushort kind, ulong view, ulong generation, Action<BinaryWriter>? payload,
        CancellationToken cancellationToken, Action<CefProtocol.Frame?, Exception?>? onLate = null) {
        CefPendingCommand command = await StartCommandAsync(kind, view, generation, payload, "awaited", onLate, cancellationToken).ConfigureAwait(false);
        bool abandoned = false;
        try {
            return await command.Reply.Task.WaitAsync(_options.CommandTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // The caller stopped waiting; the companion may still execute (or already executed) the command.
            if (command.TryAbandon("caller-cancelled")) {
                abandoned = true;
                _diagnostics.CommandAbandoned(kind);
            }
            throw;
        }
        catch (TimeoutException exception) {
            var failure = new IOException(
                $"CEF command '{CefKind.Name(kind)}' was not answered within {_options.CommandTimeout}; its delivery is uncertain and it is never replayed.",
                exception);
            Fail(failure);
            throw new InfiniFrameHostOperationException(failure.Message, failure);
        }
        finally {
            if (!abandoned) {
                _pending.TryRemove(command.Request, out _);
                _slots.Release();
            }
        }
    }

    /// <summary>
    ///     Sends a command without waiting for its reply (for example, a close request: closure is reported separately).
    ///     The reply still frees its slot when it arrives, and an error reply is reported as a late fault.
    /// </summary>
    internal async Task SendDetachedAsync(ushort kind, ulong view, ulong generation, Action<BinaryWriter>? payload,
        CancellationToken cancellationToken, Action<CefProtocol.Frame?, Exception?>? onLate = null) {
        CefPendingCommand command = await StartCommandAsync(kind, view, generation, payload, "detached", onLate, cancellationToken).ConfigureAwait(false);
        if (!command.TryAbandon("detached")) {
            // Already answered (or the session ended) before it could be detached. Whoever finished it saw a waiting
            // caller and left the slot to that caller, so release it here.
            _pending.TryRemove(command.Request, out _);
            _slots.Release();
            if (command.Reply.Task.IsFaulted) {
                Exception error = command.Reply.Task.Exception!.GetBaseException();
                ReportLate(command, null, error);
            }
        }
    }

    private async Task<CefPendingCommand> StartCommandAsync(ushort kind, ulong view, ulong generation, Action<BinaryWriter>? payload,
        string reason, Action<CefProtocol.Frame?, Exception?>? onLate, CancellationToken cancellationToken) {
        ThrowIfFailed();
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        ulong id = unchecked((ulong)Interlocked.Increment(ref _request));
        var command = new CefPendingCommand(kind, id, view, reason) { OnLate = onLate };
        try {
            ThrowIfFailed();
            if (kind == CefKind.Shutdown) Volatile.Write(ref _expectedShutdown, 1);
            if (!_pending.TryAdd(id, command)) throw new InvalidOperationException("Duplicate CEF request id.");
            Send(kind, id, view, generation, payload);
            _diagnostics.CommandSent(kind);
            return command;
        }
        catch {
            _pending.TryRemove(id, out _);
            _slots.Release();
            throw;
        }
    }

    /// <summary>Queues one frame for the companion. Overflow faults the session; nothing is silently dropped.</summary>
    internal void Send(ushort kind, ulong request, ulong view, ulong generation, Action<BinaryWriter>? payload = null) {
        ThrowIfFailed();
        byte[] frame = CefProtocol.Encode(kind, request, view, generation, payload);
        long bytes = Interlocked.Add(ref _queuedOutputBytes, frame.Length);
        UpdatePeak();
        if (bytes <= CefProtocol.MaxQueuedBytes && _outgoing.Writer.TryWrite(frame)) return;
        Interlocked.Add(ref _queuedOutputBytes, -frame.Length);
        var exception = new IOException("The CEF outgoing queue limit (256 frames / 64 MiB) was reached. The session is faulted; no message is silently discarded.");
        Fail(exception);
        throw new InfiniFrameHostOperationException(exception.Message, exception);
    }

    /// <summary>Test seam: writes raw bytes to the companion's input, bypassing framing (partial-frame tests).</summary>
    internal async Task WriteRawForTestingAsync(byte[] bytes) {
        await _process.StandardInput.BaseStream.WriteAsync(bytes).ConfigureAwait(false);
        await _process.StandardInput.BaseStream.FlushAsync().ConfigureAwait(false);
    }

    private void UpdatePeak() {
        long value = QueuedBytes;
        long current = Interlocked.Read(ref _peakQueuedBytes);
        while (value > current) {
            long observed = Interlocked.CompareExchange(ref _peakQueuedBytes, value, current);
            if (observed == current) break;
            current = observed;
        }
    }

    private async Task ReadLoop() {
        _diagnostics.ObserveThread("read-loop");
        try {
            Stream input = _process.StandardOutput.BaseStream;
            while (!_stop.IsCancellationRequested) {
                CefProtocol.Frame frame = await CefProtocol.ReadAsync(input, _stop.Token).ConfigureAwait(false);
                _diagnostics.FrameReceived(frame.Kind);
                switch (frame.Kind) {
                    case CefKind.Ok or CefKind.Error:
                        CompleteCommand(frame);
                        break;
                    case CefKind.ResourceCancelled:
                        lock (_resourceGate) {
                            if (_activeResources.TryGetValue(frame.Request, out CefResourceLease? lease)) {
                                lease.MarkCancelledByCompanion();
                                _ = ObserveCancellation(lease.Source.CancelAsync());
                            }
                        }
                        break;
                    case CefKind.ResourceRequest:
                        EnqueueResource(frame);
                        break;
                    case CefKind.DocumentAttached or CefKind.Message or CefKind.Closed or CefKind.ViewFailed or CefKind.DocumentRetired:
                        if (_router.OnEvent(frame) is {} work) EnqueueEvent(work, frame.Body.Length);
                        break;
                    default:
                        throw new InvalidDataException($"Unknown CEF frame kind {frame.Kind}.");
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) {}
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
            bool orderlyEnd = exception is EndOfStreamException
                && (Volatile.Read(ref _disposing) != 0 || Volatile.Read(ref _expectedShutdown) != 0);
            if (orderlyEnd || Volatile.Read(ref _disposing) != 0) return;
            // An unexpected end of the companion's output usually means it is exiting: let the exit observer report the
            // exit code and the companion's last diagnostics first (bounded), not a bare end-of-stream.
            if (exception is EndOfStreamException or IOException) await ReportAfterExitAsync().ConfigureAwait(false);
            Fail(exception);
        }
    }

    private void CompleteCommand(CefProtocol.Frame frame) {
        if (!_pending.TryRemove(frame.Request, out CefPendingCommand? command)) {
            _diagnostics.UnmatchedReply();
            _logger.LogDebug("Ignored a CEF reply for unknown request {Request}", frame.Request);
            return;
        }
        Exception? error = null;
        if (frame.Kind == CefKind.Error) {
            using BinaryReader reader = frame.OpenPayload();
            error = new CefCommandException(command.Kind, reader.ReadStringValue());
        }
        switch (command.Finish()) {
            case 0:
                if (error is null) command.Reply.TrySetResult(frame);
                else {
                    command.Reply.TrySetException(error);
                    _ = command.Reply.Task.Exception;// observed here too: a racing detach may never await it
                }
                break;
            case 1:
                _slots.Release();
                ReportLate(command, error is null ? frame : null, error);
                break;
        }
    }

    private void ReportLate(CefPendingCommand command, CefProtocol.Frame? reply, Exception? error) {
        _diagnostics.LateCompletion(command, error);
        if (error is CefCommandException { Error: CefCommandException.DocumentRetired })
            _logger.LogDebug("An abandoned CEF '{Kind}' command was rejected for a retired document (expected after replacement)", CefKind.Name(command.Kind));
        else if (error is not null && command.Reason != "detached")
            _logger.LogWarning("A CEF '{Kind}' command finished with an error after its caller stopped waiting: {Error}", CefKind.Name(command.Kind), error.Message);
        else if (error is not null)
            _logger.LogWarning("A detached CEF '{Kind}' command failed: {Error}", CefKind.Name(command.Kind), error.Message);
        try {
            command.OnLate?.Invoke(reply, error);
            _router.OnLateCompletion(command, reply, error);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
            _logger.LogWarning(exception, "Handling a late CEF completion failed");
        }
    }

    private void EnqueueResource(CefProtocol.Frame frame) {
        long bytes = Interlocked.Add(ref _queuedInputBytes, frame.Body.Length);
        UpdatePeak();
        lock (_resourceGate) {
            if (_activeResources.Count >= MaxActiveResources || _activeResources.ContainsKey(frame.Request))
                throw new InvalidDataException("The CEF resource request limit was exceeded or a request id was duplicated.");
            if (bytes > CefProtocol.MaxQueuedBytes) throw new IOException("The CEF incoming queue limit was reached.");
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            cancellation.CancelAfter(_options.ResourceTimeout);
            var lease = new CefResourceLease(cancellation);
            _activeResources.Add(frame.Request, lease);
            if (!_resources.Writer.TryWrite(new ResourceWork(frame, lease))) {
                _activeResources.Remove(frame.Request);
                cancellation.Dispose();
                throw new IOException("The CEF resource queue is full.");
            }
        }
    }

    private void EnqueueEvent(Action work, int bytes) {
        long queued = Interlocked.Add(ref _queuedInputBytes, bytes);
        UpdatePeak();
        if (queued <= CefProtocol.MaxQueuedBytes && _events.Writer.TryWrite(new QueuedEvent(work, bytes))) return;
        Interlocked.Add(ref _queuedInputBytes, -bytes);
        throw new IOException("The CEF incoming event queue limit (256 frames / 64 MiB) was reached.");
    }

    private async Task WriteLoop() {
        _diagnostics.ObserveThread("write-loop");
        try {
            Stream output = _process.StandardInput.BaseStream;
            await foreach (byte[] frame in _outgoing.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false)) {
                try {
                    await output.WriteAsync(frame, _stop.Token).ConfigureAwait(false);
                    await output.FlushAsync(_stop.Token).ConfigureAwait(false);
                }
                finally { Interlocked.Add(ref _queuedOutputBytes, -frame.Length); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) {}
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
            if (Volatile.Read(ref _disposing) != 0) return;
            // A broken pipe usually means the companion is exiting: report its exit code and diagnostics first.
            if (exception is IOException) await ReportAfterExitAsync().ConfigureAwait(false);
            Fail(exception);
        }
    }

    /// <summary>Waits (at most 5 s) until the exit observer has reported the companion's exit, if it is exiting.</summary>
    private Task ReportAfterExitAsync() => Task.WhenAny(_exitReported.Task, Task.Delay(TimeSpan.FromSeconds(5)));

    private async Task EventLoop() {
        try {
            await foreach (QueuedEvent item in _events.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false)) {
                _diagnostics.ObserveThread("event-loop");
                try { item.Work(); }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
                    // The InfiniFrame sink never throws by contract; a throw means the session state is unknown.
                    Fail(new InvalidOperationException("A CEF view event could not be delivered to InfiniFrame.", exception));
                }
                finally { Interlocked.Add(ref _queuedInputBytes, -item.Bytes); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) {}
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
            if (Volatile.Read(ref _disposing) == 0) Fail(exception);
        }
    }

    private async Task ResourceLoop() {
        try {
            await foreach (ResourceWork work in _resources.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false)) {
                _diagnostics.ObserveThread("resource-worker");
                try { await _router.ServeResourceAsync(work.Frame, work.Lease).ConfigureAwait(false); }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
                    // Per-request failures are answered inside ServeResourceAsync; reaching here is a relay defect.
                    _logger.LogError(exception, "A CEF resource request failed outside its own error handling");
                }
                finally {
                    lock (_resourceGate) {
                        _activeResources.Remove(work.Frame.Request);
                        work.Lease.Source.Dispose();
                    }
                    Interlocked.Add(ref _queuedInputBytes, -work.Frame.Body.Length);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) {}
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
            if (Volatile.Read(ref _disposing) == 0) Fail(exception);
        }
    }

    private async Task DrainErrors() {
        try {
            // A fixed buffer, never ReadLine/ReadToEnd: a child can emit an unbounded line.
            char[] buffer = new char[2048];
            int count;
            while ((count = await _process.StandardError.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) != 0)
                Stderr.Append(buffer.AsSpan(0, count));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) {
            _logger.LogDebug(exception, "The CEF companion stderr stream ended with an error");
        }
        finally { Stderr.Flush(); }
    }

    private async Task ObserveExit() {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        // From the job's PID list, excluding the companion itself (job accounting can lag its exit briefly).
        _diagnostics.OwnedProcessesAtCompanionExit(_tree.ProcessIds() is {} owned ? owned.Count(id => id != ProcessId) : _tree.ActiveProcesses);
        // Let the stderr drain reach EOF so a failure report carries the companion's last diagnostics.
        await Task.WhenAny(_stderrTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        int exitCode = _process.ExitCode;
        _diagnostics.CompanionExited(exitCode);
        bool orderly = exitCode == 0 && (Volatile.Read(ref _disposing) != 0 || Volatile.Read(ref _expectedShutdown) != 0)
            && _pending.Values.All(static command => command.Abandoned);
        // Terminated by this provider's own disposal escalation: already reported as ExitEscalated, not a session fault.
        if (orderly || Volatile.Read(ref _escalating) != 0) _completion.TrySetResult();
        else Fail(new IOException($"The CEF companion exited with code {exitCode}; outstanding operations were faulted. Recent companion diagnostics: {Stderr.Summary(8)}"));
        _exitReported.TrySetResult();
    }

    /// <summary>The first failure wins: every waiter fails, abandoned commands are reported, the session stops.</summary>
    internal void Fail(Exception exception) {
        if (Interlocked.CompareExchange(ref _failure, exception, null) is not null) return;
        _logger.LogError(exception, "The CEF companion session failed. Recent companion diagnostics: {Stderr}", Stderr.Summary(8));
        _diagnostics.SessionFaulted(exception);
        FinishAllPending(exception, "session-ended");
        _outgoing.Writer.TryComplete(exception);
        _events.Writer.TryComplete(exception);
        _resources.Writer.TryComplete(exception);
        // Cancellation callbacks run off this (possibly read-loop) thread.
        try { _ = ObserveCancellation(_stop.CancelAsync()); }
        catch (ObjectDisposedException) {}
        _completion.TrySetException(exception);
    }

    private void FinishAllPending(Exception exception, string reason) {
        foreach (ulong id in _pending.Keys.ToArray()) {
            if (!_pending.TryRemove(id, out CefPendingCommand? command)) continue;
            switch (command.Finish()) {
                case 0:
                    // Callers see the contract's operation exception (the cause is kept), never a raw transport exception.
                    command.Reply.TrySetException(exception is InfiniFrameHostOperationException or ObjectDisposedException
                        ? exception
                        : new InfiniFrameHostOperationException($"The CEF companion session failed: {exception.Message}", exception));
                    _ = command.Reply.Task.Exception;// observed here too: a racing detach may never await it
                    break;
                case 1:
                    _slots.Release();
                    _diagnostics.AbandonedAtSessionEnd(command, reason);
                    try {
                        command.OnLate?.Invoke(null, exception);
                        _router.OnLateCompletion(command, null, exception);
                    }
                    catch (Exception callback) when (callback is not OutOfMemoryException and not StackOverflowException) {
                        _logger.LogWarning(callback, "Handling an abandoned CEF command at session end failed");
                    }
                    break;
            }
        }
    }

    private async Task ObserveCancellation(Task cancellation) {
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
            _logger.LogWarning(exception, "A CEF cancellation callback failed");
        }
    }

    private void ThrowIfFailed() {
        if (Volatile.Read(ref _disposing) != 0) throw new ObjectDisposedException(nameof(CefConnection));
        if (Volatile.Read(ref _failure) is {} failure)
            throw new InfiniFrameHostOperationException("The CEF companion session has failed.", failure);
    }

    /// <summary>
    ///     Orderly first (input closed, so the companion closes its views and exits), bounded by
    ///     <see cref="CefHostOptions.ExitTimeout" />; then, only if needed, terminates the companion's own process tree
    ///     and reports that escalation. The companion's descendants (CEF's child processes) must also be gone within the
    ///     same bound, or the owned tree is terminated and that is reported too. Never blocks indefinitely.
    /// </summary>
    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposing, 1) != 0) return;
        _outgoing.Writer.TryComplete();
        try {
            // Flush frames already queued (for example, the shutdown command), then signal EOF.
            await Task.WhenAny(_workers[1], Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            _process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException) {
            _logger.LogDebug(exception, "Closing the CEF companion input failed");
        }
        if (!await WaitForExitAsync(_options.ExitTimeout).ConfigureAwait(false)) {
            Volatile.Write(ref _escalating, 1);
            _diagnostics.ExitEscalated(ProcessId);
            _logger.LogWarning("The CEF companion {ProcessId} did not exit within {Timeout}; terminating its process tree (escalation)",
                ProcessId, _options.ExitTimeout);
            try { _tree.Terminate(); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) {
                _logger.LogWarning(exception, "Terminating the CEF companion process tree failed");
            }
            if (!await WaitForExitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
                _diagnostics.ExitUnconfirmed(ProcessId);
        }
        // The profile is only safe to remove once no owned process can still hold it open.
        if (!await _tree.WaitForEmptyAsync(_options.ExitTimeout).ConfigureAwait(false)) {
            _diagnostics.DescendantsEscalated();
            _logger.LogWarning("{Count} owned CEF processes were still running {Timeout} after the companion exited; terminating the owned tree (escalation)",
                _tree.ActiveProcesses, _options.ExitTimeout);
            try { _tree.Terminate(); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) {
                _logger.LogWarning(exception, "Terminating the owned CEF process tree failed");
            }
            await _tree.WaitForEmptyAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        _diagnostics.OwnedProcessesFinal(_tree.ActiveProcesses, _tree.TotalProcesses, _tree.PeakCommit);
        await _stop.CancelAsync().ConfigureAwait(false);
        _events.Writer.TryComplete();
        _resources.Writer.TryComplete();
        FinishAllPending(new ObjectDisposedException(nameof(CefConnection)), "disposed");
        try { await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
            _logger.LogDebug(exception, "CEF session loops ended with errors during disposal");
        }
        lock (_resourceGate) {
            foreach (CefResourceLease resource in _activeResources.Values) resource.Source.Dispose();
            _activeResources.Clear();
        }
        while (_events.Reader.TryRead(out _)) {}
        while (_resources.Reader.TryRead(out _)) {}
        while (_outgoing.Reader.TryRead(out _)) {}
        Interlocked.Exchange(ref _queuedInputBytes, 0);
        Interlocked.Exchange(ref _queuedOutputBytes, 0);
        _completion.TrySetResult();
        _process.Dispose();
        _tree.Dispose();
        _stop.Dispose();
    }

    private async Task<bool> WaitForExitAsync(TimeSpan timeout) {
        try {
            await _process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) {
            return false;
        }
    }

    private readonly record struct QueuedEvent(Action Work, int Bytes);
    private sealed record ResourceWork(CefProtocol.Frame Frame, CefResourceLease Lease);
}
