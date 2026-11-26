using System.Collections.Concurrent;
using System.Diagnostics;

namespace Contoso.InfiniFrame.CefHost;

/// <summary>A point-in-time snapshot of one CEF host run. Contains counts, fixed reasons and redacted text only.</summary>
public sealed class CefHostDiagnostics {
    /// <summary>The companion's process id, or 0 before it started.</summary>
    public int CompanionProcessId { get; init; }
    /// <summary>The companion's exit code, when it has exited.</summary>
    public int? CompanionExitCode { get; init; }
    /// <summary>True when disposal had to terminate the companion's own process tree (not a graceful exit).</summary>
    public bool ExitEscalated { get; init; }
    /// <summary>True when even termination could not be confirmed within its bound.</summary>
    public bool ExitUnconfirmed { get; init; }
    /// <summary>How the companion's process tree is tracked: "job object" or "parent-process walk (reason)".</summary>
    public string ProcessTracking { get; init; } = "pending";
    /// <summary>Every process that belonged to the owned tree (companion included); null without a job.</summary>
    public int? OwnedProcessesStarted { get; init; }
    /// <summary>Owned processes (CEF children) still alive at the moment the companion itself exited; null without a job.</summary>
    public int? OwnedProcessesAliveAtCompanionExit { get; init; }
    /// <summary>Owned processes alive when disposal finished (0 expected); null without a job or before disposal.</summary>
    public int? OwnedProcessesRemaining { get; init; }
    /// <summary>True when owned descendants outlived the companion beyond the exit bound and the owned tree was terminated.</summary>
    public bool DescendantsEscalated { get; init; }
    /// <summary>Peak committed memory of the whole owned tree, in bytes; null without a job.</summary>
    public long? PeakTreeCommitBytes { get; init; }
    /// <summary>Peak committed memory of the largest single owned process, in bytes; null without a job.</summary>
    public long? PeakProcessCommitBytes { get; init; }
    /// <summary>Frames sent to the companion, by kind name.</summary>
    public IReadOnlyDictionary<string, long> CommandsSent { get; init; } = new Dictionary<string, long>();
    /// <summary>Frames received from the companion, by kind name.</summary>
    public IReadOnlyDictionary<string, long> FramesReceived { get; init; } = new Dictionary<string, long>();
    /// <summary>Blazor/InfiniFrame IPC message types seen per direction (names only, never payloads).</summary>
    public IReadOnlyDictionary<string, long> MessageTypes { get; init; } = new Dictionary<string, long>();
    /// <summary>Commands whose caller stopped waiting while the companion still owed an answer.</summary>
    public long CommandsAbandoned { get; init; }
    /// <summary>Abandoned or detached commands that the companion later answered successfully.</summary>
    public long LateCompletions { get; init; }
    /// <summary>Abandoned or detached commands that the companion later answered with an error.</summary>
    public IReadOnlyList<string> LateFaults { get; init; } = [];
    /// <summary>
    ///     Abandoned sends the companion later rejected because their document had retired: the documented, expected
    ///     outcome of a document replacement (not a fault).
    /// </summary>
    public long LateRetiredRejections { get; init; }
    /// <summary>Abandoned commands still unanswered when the session ended.</summary>
    public long AbandonedAtSessionEnd { get; init; }
    /// <summary>Replies that matched no outstanding command.</summary>
    public long UnmatchedReplies { get; init; }
    /// <summary>Outstanding commands now (including abandoned ones).</summary>
    public int PendingCommands { get; init; }
    /// <summary>Current and peak queued transport bytes.</summary>
    public long QueuedBytes { get; init; }
    /// <summary>Peak queued transport bytes.</summary>
    public long PeakQueuedBytes { get; init; }
    /// <summary>Resource responses sent, by HTTP status.</summary>
    public IReadOnlyDictionary<int, long> ResourceStatuses { get; init; } = new Dictionary<int, long>();
    /// <summary>Resource paths answered (no query or fragment) with their last status; first 128 distinct paths.</summary>
    public IReadOnlyDictionary<string, int> ResourcePaths { get; init; } = new Dictionary<string, int>();
    /// <summary>Resource requests the provider had to answer with its own error, by fixed reason.</summary>
    public IReadOnlyDictionary<string, long> ResourceRelayErrors { get; init; } = new Dictionary<string, long>();
    /// <summary>Documents attached and retired, and stale messages dropped before reaching InfiniFrame.</summary>
    public long DocumentsAttached { get; init; }
    /// <summary>Documents retired.</summary>
    public long DocumentsRetired { get; init; }
    /// <summary>Messages from a non-current native document, dropped by the provider.</summary>
    public long StaleMessagesDropped { get; init; }
    /// <summary>Messages sent to (and received from) each public document, keyed "view/document".</summary>
    public IReadOnlyDictionary<string, long> SendsByDocument { get; init; } = new Dictionary<string, long>();
    /// <summary>Messages received from each public document, keyed "view/document".</summary>
    public IReadOnlyDictionary<string, long> ReceivedByDocument { get; init; } = new Dictionary<string, long>();
    /// <summary>Ordered document attach/retire events and the last send per document (bounded timeline, milliseconds since start).</summary>
    public IReadOnlyList<string> DocumentTimeline { get; init; } = [];
    /// <summary>Windows created and closed natively.</summary>
    public long ViewsCreated { get; init; }
    /// <summary>Windows the companion reported closed.</summary>
    public long ViewsClosed { get; init; }
    /// <summary>Windows whose late native creation was reclaimed after their caller gave up.</summary>
    public long ViewsReclaimed { get; init; }
    /// <summary>Window-level faults reported to InfiniFrame (fixed reasons).</summary>
    public IReadOnlyList<string> ViewFaults { get; init; } = [];
    /// <summary>Session faults (host-level).</summary>
    public IReadOnlyList<string> SessionFaults { get; init; } = [];
    /// <summary>Managed thread ids observed per provider role (bounded).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> ThreadsByRole { get; init; } = new Dictionary<string, IReadOnlyList<int>>();
    /// <summary>The retained, redacted companion stderr tail.</summary>
    public IReadOnlyList<string> CompanionStderrTail { get; init; } = [];
    /// <summary>Stderr lines dropped (beyond the retained tail) and truncated.</summary>
    public long StderrLinesDropped { get; init; }
    /// <summary>Stderr lines truncated to the per-line limit.</summary>
    public long StderrLinesTruncated { get; init; }
    /// <summary>The per-run state directory (profile location) and what cleanup found and did.</summary>
    public string? RunDirectory { get; init; }
    /// <summary>Run directories of ended applications removed at start (exact provider-named entries only; bounded).</summary>
    public IReadOnlyList<string> StaleRunsReclaimed { get; init; } = [];
    /// <summary>Cleanup outcome: pending, clean, removed-after-exit, removed-leftovers, failed (with reason).</summary>
    public string Cleanup { get; init; } = "pending";
}

internal sealed class CefDiagnosticsCollector {
    private const int MaxListed = 32;
    private readonly ConcurrentDictionary<string, long> _sent = new();
    private readonly ConcurrentDictionary<string, long> _received = new();
    private readonly ConcurrentDictionary<string, long> _messageTypes = new();
    private readonly ConcurrentDictionary<int, long> _resourceStatuses = new();
    private readonly ConcurrentDictionary<string, long> _relayErrors = new();
    private readonly ConcurrentDictionary<string, int> _resourcePaths = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>> _threads = new();
    private readonly ConcurrentQueue<string> _lateFaults = new();
    private readonly ConcurrentQueue<string> _viewFaults = new();
    private readonly ConcurrentQueue<string> _sessionFaults = new();
    private readonly ConcurrentDictionary<string, long> _sendsByDocument = new();
    private readonly ConcurrentDictionary<string, long> _receivedByDocument = new();
    private readonly ConcurrentQueue<string> _timeline = new();
    private readonly long _origin = Stopwatch.GetTimestamp();
    private int _processId;
    private int? _exitCode;
    private int _escalated;
    private int _unconfirmed;
    private int _descendantsEscalated;
    private string _tracking = "pending";
    private int? _aliveAtExit, _ownedStarted, _ownedRemaining;
    private long? _peakTree, _peakProcess;
    private long _abandoned, _late, _lateRetired, _abandonedAtEnd, _unmatched, _attached, _retired, _stale, _created, _closed, _reclaimed;
    private string? _runDirectory;
    private IReadOnlyList<string> _staleRuns = [];
    private string _cleanup = "pending";

    internal void CompanionStarted(int processId) => Volatile.Write(ref _processId, processId);
    internal void CompanionExited(int exitCode) => _exitCode = exitCode;
    internal void ExitEscalated(int processId) => Volatile.Write(ref _escalated, 1);
    internal void ExitUnconfirmed(int processId) => Volatile.Write(ref _unconfirmed, 1);
    internal void ProcessTracking(string tracking) => _tracking = tracking;
    internal void OwnedProcessesAtCompanionExit(int? alive) => _aliveAtExit = alive;
    internal void DescendantsEscalated() => Volatile.Write(ref _descendantsEscalated, 1);
    internal void OwnedProcessesFinal(int? remaining, int? started, (long? Tree, long? LargestProcess) peak) {
        _ownedRemaining = remaining;
        _ownedStarted = started;
        (_peakTree, _peakProcess) = peak;
    }
    internal void CommandSent(ushort kind) => _sent.AddOrUpdate(CefKind.Name(kind), 1, static (_, v) => v + 1);
    internal void FrameReceived(ushort kind) => _received.AddOrUpdate(CefKind.Name(kind), 1, static (_, v) => v + 1);
    internal void CommandAbandoned(ushort kind) => Interlocked.Increment(ref _abandoned);
    internal void UnmatchedReply() => Interlocked.Increment(ref _unmatched);
    internal void DocumentAttached(ulong view, ulong document) {
        Interlocked.Increment(ref _attached);
        Timeline($"{Now()} view {view} document {document} attached");
    }
    internal void DocumentRetired(ulong view, ulong document) {
        Interlocked.Increment(ref _retired);
        Timeline($"{Now()} view {view} document {document} retired (last send {(_sendsByDocument.TryGetValue($"{view}/{document}", out long sends) ? sends : 0)} messages)");
    }
    internal void SentTo(ulong view, ulong document) => _sendsByDocument.AddOrUpdate($"{view}/{document}", 1, static (_, v) => v + 1);
    internal void ReceivedFrom(ulong view, ulong document) => _receivedByDocument.AddOrUpdate($"{view}/{document}", 1, static (_, v) => v + 1);
    private long Now() => (long)Stopwatch.GetElapsedTime(_origin).TotalMilliseconds;
    private void Timeline(string entry) {
        _timeline.Enqueue(entry);
        while (_timeline.Count > 256 && _timeline.TryDequeue(out _)) {}
    }
    internal void StaleMessageDropped() => Interlocked.Increment(ref _stale);
    internal void ViewCreated() => Interlocked.Increment(ref _created);
    internal void ViewClosed() => Interlocked.Increment(ref _closed);
    internal void ViewReclaimed() => Interlocked.Increment(ref _reclaimed);
    internal void ResourceAnswered(int status, string path) {
        _resourceStatuses.AddOrUpdate(status, 1, static (_, v) => v + 1);
        if (_resourcePaths.Count < 128 || _resourcePaths.ContainsKey(path)) _resourcePaths[path] = status;
    }
    internal void ResourceRelayError(string reason) => _relayErrors.AddOrUpdate(reason, 1, static (_, v) => v + 1);
    internal void ViewFault(string reason) => Add(_viewFaults, reason);
    // Session faults keep up to 1,000 characters: their companion diagnostics (newest first) are the useful part.
    internal void SessionFaulted(Exception exception) => Add(_sessionFaults, $"{exception.GetType().Name}: {Truncate(exception.Message, 1000)}");
    internal void RunDirectory(string path) => _runDirectory = path;
    internal void StaleRunsReclaimed(IReadOnlyList<string> runs) => _staleRuns = runs;
    internal void Cleanup(string outcome) => _cleanup = outcome;

    internal void LateCompletion(CefPendingCommand command, Exception? error) {
        if (error is null) Interlocked.Increment(ref _late);
        else if (error is CefCommandException { Error: CefCommandException.DocumentRetired }) Interlocked.Increment(ref _lateRetired);
        else Add(_lateFaults, $"{CefKind.Name(command.Kind)} ({command.Reason}) after {Elapsed(command)} ms: {Truncate(error.Message)}");
    }

    internal void AbandonedAtSessionEnd(CefPendingCommand command, string reason) {
        Interlocked.Increment(ref _abandonedAtEnd);
        Add(_lateFaults, $"{CefKind.Name(command.Kind)} ({command.Reason}) unanswered at {reason} after {Elapsed(command)} ms");
    }

    /// <summary>Counts an IPC message type (for example <c>out:RenderBatch</c>) from a bounded prefix; never stores payloads.</summary>
    internal void MessageType(string direction, string message) {
        string type = "other";
        const string blazor = "__bwv:[\"";
        if (message.StartsWith(blazor, StringComparison.Ordinal)) {
            int end = message.IndexOf('"', blazor.Length);
            if (end > blazor.Length && end - blazor.Length <= 32) type = message[blazor.Length..end];
            else type = "blazor-other";
        }
        else if (message.StartsWith("{\"id\":\"__infiniframe", StringComparison.Ordinal)) type = "infiniframe-envelope";
        _messageTypes.AddOrUpdate($"{direction}:{type}", 1, static (_, v) => v + 1);
    }

    internal void ObserveThread(string role) {
        ConcurrentDictionary<int, byte> threads = _threads.GetOrAdd(role, static _ => new ConcurrentDictionary<int, byte>());
        if (threads.Count < 16) threads.TryAdd(Environment.CurrentManagedThreadId, 0);
    }

    internal CefHostDiagnostics Snapshot(CefConnection? connection) {
        (IReadOnlyList<string> lines, long dropped, long truncated, _) = connection?.Stderr.Snapshot() ?? ([], 0, 0, 0);
        return new CefHostDiagnostics {
            CompanionProcessId = Volatile.Read(ref _processId),
            CompanionExitCode = _exitCode,
            ExitEscalated = Volatile.Read(ref _escalated) != 0,
            ExitUnconfirmed = Volatile.Read(ref _unconfirmed) != 0,
            ProcessTracking = _tracking,
            OwnedProcessesStarted = _ownedStarted ?? connection?.Tree.TotalProcesses,
            OwnedProcessesAliveAtCompanionExit = _aliveAtExit,
            OwnedProcessesRemaining = _ownedRemaining,
            DescendantsEscalated = Volatile.Read(ref _descendantsEscalated) != 0,
            PeakTreeCommitBytes = _peakTree ?? connection?.Tree.PeakCommit.Tree,
            PeakProcessCommitBytes = _peakProcess ?? connection?.Tree.PeakCommit.LargestProcess,
            CommandsSent = new Dictionary<string, long>(_sent),
            FramesReceived = new Dictionary<string, long>(_received),
            MessageTypes = new Dictionary<string, long>(_messageTypes),
            CommandsAbandoned = Interlocked.Read(ref _abandoned),
            LateCompletions = Interlocked.Read(ref _late),
            LateRetiredRejections = Interlocked.Read(ref _lateRetired),
            LateFaults = [.. _lateFaults],
            AbandonedAtSessionEnd = Interlocked.Read(ref _abandonedAtEnd),
            UnmatchedReplies = Interlocked.Read(ref _unmatched),
            PendingCommands = connection?.PendingCommands ?? 0,
            QueuedBytes = connection?.QueuedBytes ?? 0,
            PeakQueuedBytes = connection?.PeakQueuedBytes ?? 0,
            ResourceStatuses = new Dictionary<int, long>(_resourceStatuses),
            ResourceRelayErrors = new Dictionary<string, long>(_relayErrors),
            ResourcePaths = new Dictionary<string, int>(_resourcePaths),
            DocumentsAttached = Interlocked.Read(ref _attached),
            DocumentsRetired = Interlocked.Read(ref _retired),
            StaleMessagesDropped = Interlocked.Read(ref _stale),
            SendsByDocument = new Dictionary<string, long>(_sendsByDocument),
            ReceivedByDocument = new Dictionary<string, long>(_receivedByDocument),
            DocumentTimeline = [.. _timeline],
            ViewsCreated = Interlocked.Read(ref _created),
            ViewsClosed = Interlocked.Read(ref _closed),
            ViewsReclaimed = Interlocked.Read(ref _reclaimed),
            ViewFaults = [.. _viewFaults],
            SessionFaults = [.. _sessionFaults],
            ThreadsByRole = _threads.ToDictionary(static pair => pair.Key, static pair => (IReadOnlyList<int>)[.. pair.Value.Keys.Order()]),
            CompanionStderrTail = lines,
            StderrLinesDropped = dropped,
            StderrLinesTruncated = truncated,
            RunDirectory = _runDirectory,
            StaleRunsReclaimed = _staleRuns,
            Cleanup = _cleanup
        };
    }

    private static void Add(ConcurrentQueue<string> queue, string value) {
        queue.Enqueue(value);
        while (queue.Count > MaxListed && queue.TryDequeue(out _)) {}
    }

    private static long Elapsed(CefPendingCommand command) => (long)Stopwatch.GetElapsedTime(command.StartedTimestamp).TotalMilliseconds;
    private static string Truncate(string value, int max = 240) => value.Length <= max ? value : value[..max] + "…";
}
