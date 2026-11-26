using System.Collections.Concurrent;
using System.Drawing;
using System.Security.Cryptography;
using InfiniFrame.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Contoso.InfiniFrame.CefHost;

/// <summary>
///     The process-wide CEF host: one companion process (CEF, its Views windows and its UI loop) per InfiniFrame
///     application run, reached over the inherited stdin/stdout pipes of protocol 3.
/// </summary>
internal sealed class CefHost : IInfiniFrameHost, ICefFrameRouter {
    private static readonly Point DefaultLocation = new(100, 100);
    private static readonly Size DefaultSize = new(1024, 768);
    private readonly CefHostOptions _options;
    private readonly Guid _applicationId;
    private readonly Func<string, CefLaunch>? _testLaunch;
    private readonly ConcurrentDictionary<ulong, CefWindowHost> _views = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CefConnection? _connection;
    private ILogger _logger = NullLogger.Instance;
    private string? _runDirectory;
    private long _nextView;
    private int _started;
    private int _shutdown;
    private int _disposed;

    internal CefHost(CefHostOptions options, Guid applicationId, Func<string, CefLaunch>? testLaunch) {
        _options = options;
        _applicationId = applicationId;
        _testLaunch = testLaunch;
    }

    internal CefDiagnosticsCollector Diagnostics { get; } = new();
    internal CefConnection Connection => _connection ?? throw new InvalidOperationException("The CEF host has not been started.");
    internal CefHostDiagnostics Snapshot() => Diagnostics.Snapshot(_connection);

    /// <inheritdoc />
    public Task Completion => _completion.Task;

    /// <inheritdoc />
    public async ValueTask StartAsync(InfiniFrameHostStartContext context, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(context);
        if (Interlocked.Exchange(ref _started, 1) != 0) throw new InvalidOperationException("The CEF host has already been started.");
        _logger = context.LoggerFactory.CreateLogger("Contoso.InfiniFrame.CefHost");
        string? payload = _testLaunch is null ? CefPayload.Validate(_options.PayloadDirectory) : null;
        _runDirectory = CreateRunDirectory();
        Diagnostics.RunDirectory(_runDirectory);
        CefLaunch launch = _testLaunch?.Invoke(_runDirectory) ?? ForPayload(payload!, _runDirectory);
        var connection = new CefConnection(launch, _options, _logger, Diagnostics, this);
        _connection = connection;
        using (ExecutionContext.SuppressFlow()) _ = Task.Run(() => LinkCompletionAsync(connection));
        // On failure InfiniFrame still disposes the host, which stops the companion and cleans the run directory.
        await connection.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("CEF companion {ProcessId} started for application {ApplicationId}", connection.ProcessId, _applicationId);
    }

    private string CreateRunDirectory() {
        Directory.CreateDirectory(_options.StateRoot);
        Diagnostics.StaleRunsReclaimed(ReclaimStaleRuns());
        string run = Path.Combine(_options.StateRoot,
            $"run-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{Environment.ProcessId}-{RandomNumberGenerator.GetHexString(8, lowercase: true)}");
        if (Directory.Exists(run)) throw new IOException("The CEF run directory already exists.");
        Directory.CreateDirectory(Path.Combine(run, "state"));
        return run;
    }

    /// <summary>
    ///     Removes run directories left by applications that ended without disposing their host (a killed process,
    ///     for example; its companion tree already ended with the job). Only entries named exactly as this provider names
    ///     runs are considered, and only when their owning process is gone or its id now belongs to a newer process.
    ///     Runs of live applications, including this process, are never touched. Returns what was removed (or why not).
    /// </summary>
    private List<string> ReclaimStaleRuns() {
        var reclaimed = new List<string>();
        foreach (string directory in Directory.EnumerateDirectories(_options.StateRoot, "run-*")) {
            string name = Path.GetFileName(directory);
            string[] parts = name.Split('-');
            if (parts.Length != 4 || parts[3].Length != 8 || !parts[3].All(char.IsAsciiHexDigitLower)
                || !DateTime.TryParseExact(parts[1], "yyyyMMdd'T'HHmmssfff", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime created)
                || !int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int owner)
                || owner == Environment.ProcessId || OwnerAlive(owner, created))
                continue;
            try {
                Directory.Delete(directory, recursive: true);
                reclaimed.Add(name);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                reclaimed.Add($"{name} (not removed: {exception.GetType().Name})");
            }
            if (reclaimed.Count >= 16) break;
        }
        return reclaimed;
    }

    private static bool OwnerAlive(int processId, DateTime runCreatedUtc) {
        try {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime() <= runCreatedUtc.AddSeconds(1);
        }
        catch (ArgumentException) {
            return false;// no such process
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) {
            return true;// cannot tell: keep it
        }
    }

    private static CefLaunch ForPayload(string payload, string run) {
        string host = Path.Combine(payload, OperatingSystem.IsWindows() ? "InfiniFrame.Cef.Host.exe" : "InfiniFrame.Cef.Host");
        // The companion creates its private profile under this per-run location (exact path, removed after exit).
        var environment = new Dictionary<string, string> {
            [OperatingSystem.IsWindows() ? "LOCALAPPDATA" : "XDG_STATE_HOME"] = Path.Combine(run, "state")
        };
        return new CefLaunch(host, [], run, environment);
    }

    private async Task LinkCompletionAsync(CefConnection connection) {
        try {
            await connection.Completion.ConfigureAwait(false);
            _completion.TrySetResult();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
            _completion.TrySetException(new InfiniFrameHostOperationException($"The CEF companion session ended: {exception.Message}", exception));
        }
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Windows
    // -----------------------------------------------------------------------------------------------------------------
    /// <inheritdoc />
    public async ValueTask<IInfiniFrameWindowHost> CreateWindowAsync(InfiniFrameHostWindowRequest request,
        IInfiniFrameWindowHostEvents events, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);
        List<string> violations = Validate(request, out Rectangle bounds);
        if (violations.Count != 0)
            throw new NotSupportedException(
                $"The CEF companion cannot honour this window exactly: {string.Join("; ", violations)}. Nothing was created.");
        CefWindowStyle style = CefWindowStyle.From(request);
        CefConnection connection = Connection;
        ulong view = checked((ulong)Interlocked.Increment(ref _nextView));
        var window = new CefWindowHost(this, view, events, _logger);
        // Registered before the create command: the companion's events may precede its reply.
        if (!_views.TryAdd(view, window)) throw new InvalidOperationException("Duplicate CEF view id.");
        try {
            await connection.RequestAsync(CefKind.Create, view, 0, writer => {
                writer.WriteString(request.Title);
                writer.Write(bounds.X);
                writer.Write(bounds.Y);
                writer.Write(bounds.Width);
                writer.Write(bounds.Height);
                writer.WriteString(request.StartUrl.AbsoluteUri);
                writer.WriteString(request.BridgeScript);
                style.Write(writer);
            }, cancellationToken, onLate: (reply, error) => OnLateCreate(window, reply, error)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // The native window may still be created; OnLateCreate reclaims it when the reply arrives.
            window.DetachFromFacade();
            throw;
        }
        catch (CefCommandException exception) {
            window.DetachFromFacade();
            throw new InfiniFrameHostOperationException($"The CEF companion could not create the window: {exception.Error}.", exception);
        }
        catch {
            window.DetachFromFacade();
            throw;
        }
        Diagnostics.ViewCreated();
        try {
            // Confirmed initial geometry before Created(), as the companion's create contract requires.
            await window.SetBoundsAsync(bounds, cancellationToken).ConfigureAwait(false);
            if (!request.InitialIconPng.IsEmpty) await window.SetIconAsync(request.InitialIconPng, cancellationToken).ConfigureAwait(false);
            if (request.InitialState.HasFlag(InfiniFrameHostWindowState.FullScreen))
                await window.SetStateAsync(InfiniFrameHostWindowStateChange.EnterFullScreen, cancellationToken).ConfigureAwait(false);
            if (request.InitialState.HasFlag(InfiniFrameHostWindowState.Maximized))
                await window.SetStateAsync(InfiniFrameHostWindowStateChange.Maximize, cancellationToken).ConfigureAwait(false);
            if (request.InitialState.HasFlag(InfiniFrameHostWindowState.Minimized))
                await window.SetStateAsync(InfiniFrameHostWindowStateChange.Minimize, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
            // The native window exists: close it rather than abandon it, then report the failed creation.
            window.DetachFromFacade();
            await CloseFailedCreationAsync(window).ConfigureAwait(false);
            if (exception is OperationCanceledException or InfiniFrameHostOperationException) throw;
            throw new InfiniFrameHostOperationException("The CEF window could not be initialised.", exception);
        }
        // Like every other sink event, Created() runs without the caller's ExecutionContext (no AsyncLocal flows in).
        Task created;
        using (ExecutionContext.SuppressFlow()) created = Task.Run(events.Created);
        await created.ConfigureAwait(false);
        return window;
    }

    private void OnLateCreate(CefWindowHost window, CefProtocol.Frame? reply, Exception? error) {
        if (reply is null) return;// Creation failed natively or the session ended: there is no native window to reclaim.
        using (ExecutionContext.SuppressFlow()) _ = Task.Run(window.ReclaimAsync);
    }

    private async Task CloseFailedCreationAsync(CefWindowHost window) {
        try {
            await window.RequestCloseAsync(CancellationToken.None).ConfigureAwait(false);
            await window.NativeClosed.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
            _logger.LogWarning(exception, "A CEF window whose initialisation failed could not be confirmed closed (view {View})", window.View);
        }
    }

    internal void ForgetView(CefWindowHost window) => _views.TryRemove(window.View, out _);

    /// <summary>Request checks: the companion must honour every field exactly or reject the window before creating it.</summary>
    internal static List<string> Validate(InfiniFrameHostWindowRequest request, out Rectangle bounds) {
        var violations = new List<string>();
        if (!CefProtocol.IsApplicationUri(request.StartUrl.AbsoluteUri))
            violations.Add($"start URL {request.StartUrl} (the companion serves only app://localhost/)");
        if (!request.CustomSchemes.Contains("app")) violations.Add("no 'app' scheme handler is registered to serve app://localhost/");
        foreach (string scheme in request.CustomSchemes.Where(static scheme => scheme != "app"))
            violations.Add($"custom scheme '{scheme}' (the companion routes only app://localhost/)");
        if (request.TrustAllOrigins) violations.Add("trusting every origin");
        foreach (Uri origin in request.TrustedOrigins.Where(static origin => origin.Scheme != "app" || origin.Authority != "localhost"))
            violations.Add($"trusted origin {origin} (only app://localhost/ is supported)");
        if (request.BridgeContractVersion != InfiniFrameHostContract.BridgeContractVersion)
            violations.Add($"bridge contract {request.BridgeContractVersion} (the companion implements {InfiniFrameHostContract.BridgeContractVersion})");
        if (string.IsNullOrEmpty(request.BridgeScript)) violations.Add("an empty bridge script");
        if (CefProtocol.Utf8.GetByteCount(request.Title) > CefWindowHost.MaxTitleBytes) violations.Add("a title longer than 4096 UTF-8 bytes");
        InfiniFrameHostBrowserPolicy policy = request.BrowserPolicy;
        if (!policy.WebSecurity) violations.Add("disabled web security");
        if (policy.DevTools) violations.Add("DevTools (call EnableDevTools(false))");
        if (policy.ContextMenu) violations.Add("the browser context menu (the companion always removes it; call EnableContextMenu(false))");
        if (policy.GrantBrowserPermissions) violations.Add("automatic browser permissions (call EnableBrowserPermissions(false))");
        if (policy.JavaScriptClipboardAccess) violations.Add("script clipboard access (call EnableJavascriptClipboardAccess(false))");
        if (policy.MediaStream) violations.Add("camera/microphone streams (call EnableMediaStream(false))");
        if (policy.MediaAutoplay) violations.Add("unrestricted media autoplay (the engine's default policy applies; call EnableMediaAutoplay(false))");
        if (policy.FileSystemAccess) violations.Add("the File System Access API (call EnableFileSystemAccess(false))");
        if (policy.IgnoreCertificateErrors) violations.Add("ignoring certificate errors (call EnableIgnoreCertificateErrors(false))");
        if (policy.UserAgent is not null) violations.Add("a custom user agent (call SetUserAgent(\"\"))");
        CefWindowStyle style = CefWindowStyle.From(request);
        // InfiniFrame clamps a size it sends; the provider's own default is clamped here the same way (maximum first).
        bounds = new Rectangle(request.InitialLocation ?? DefaultLocation, request.InitialSize ?? style.Clamp(DefaultSize));
        if (!CefWindowHost.SupportedBounds(bounds)) violations.Add($"initial bounds {bounds} (positions within ±100000, sizes 64..16384)");
        violations.AddRange(style.Violations(bounds.Size));
        if (!request.InitialIconPng.IsEmpty && CefWindowHost.ValidateIcon(request.InitialIconPng.Span) is {} icon) violations.Add(icon);
        return violations;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Frame routing (ICefFrameRouter)
    // -----------------------------------------------------------------------------------------------------------------
    Action? ICefFrameRouter.OnEvent(CefProtocol.Frame frame) =>
        _views.TryGetValue(frame.View, out CefWindowHost? window)
            ? window.OnFrameRead(frame)
            : throw new InvalidDataException("A CEF event references an unknown view.");

    Task ICefFrameRouter.ServeResourceAsync(CefProtocol.Frame frame, CefResourceLease lease) {
        _views.TryGetValue(frame.View, out CefWindowHost? window);
        return CefResourceRelay.ServeAsync(this, window, window?.Events, frame, lease, _logger);
    }

    void ICefFrameRouter.OnLateCompletion(CefPendingCommand command, CefProtocol.Frame? reply, Exception? error) {
        if (error is not null && command.Reason == "caller-cancelled")
            _logger.LogInformation("A CEF '{Kind}' command for view {View} failed after its caller stopped waiting: {Error}",
                CefKind.Name(command.Kind), command.View, error.Message);
    }

    // -----------------------------------------------------------------------------------------------------------------
    // Shutdown and disposal
    // -----------------------------------------------------------------------------------------------------------------
    /// <inheritdoc />
    public async ValueTask ShutdownAsync(CancellationToken cancellationToken) {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0 || _connection is null || _connection.IsFaulted) return;
        try {
            await _connection.RequestAsync(CefKind.Shutdown, 0, 0, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
            _logger.LogWarning(exception, "The CEF companion did not acknowledge shutdown; disposal will stop it");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_connection is not null) await _connection.DisposeAsync().ConfigureAwait(false);
        _completion.TrySetResult();
        await CleanupRunDirectoryAsync().ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes exactly this run's state directory, after every owned process has exited (see
    ///     <see cref="CefConnection.DisposeAsync" />). The companion removes its own profile when it can; anything it
    ///     left (Chromium children still holding files, a crash, an escalated termination) is removed here at its
    ///     exact path and reported, never hidden.
    /// </summary>
    private async Task CleanupRunDirectoryAsync() {
        if (_runDirectory is null) return;
        string profiles = Path.Combine(_runDirectory, "state", "InfiniFrame", "Cef", "Profiles");
        string[] left = Directory.Exists(profiles) ? Directory.GetDirectories(profiles) : [];
        string outcome = left.Length == 0 ? "clean: the companion removed its profile" : "";
        // Record what the companion left (bounded relative paths) before removing it, so a cleanup race is diagnosable.
        string leftovers = string.Join(", ", left.SelectMany(static directory => Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Select(entry => Path.GetRelativePath(directory, entry).Replace(Path.DirectorySeparatorChar, '/'))).Take(12));
        int removed = 0;
        string? failure = null;
        foreach (string directory in left) {
            for (int attempt = 1; attempt <= 10; attempt++) {
                try {
                    Directory.Delete(directory, recursive: true);
                    removed++;
                    break;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                    failure = exception.Message;
                    await Task.Delay(200).ConfigureAwait(false);
                }
            }
        }
        if (left.Length != 0) {
            CefHostDiagnostics state = Snapshot();
            bool escalated = state.ExitEscalated || state.DescendantsEscalated;
            // Chromium's child processes hold profile files until they exit, which can be after the companion's own
            // in-process removal ran. With a job, every owned process is known to be gone before this point.
            bool normalExitThenOwnedGone = !escalated && state.CompanionExitCode == 0 && state.OwnedProcessesRemaining == 0;
            outcome = removed != left.Length
                ? $"failed: {left.Length - removed} of {left.Length} leftover profile directories could not be removed ({failure})"
                : normalExitThenOwnedGone
                ? $"removed-after-exit: the companion exited normally but its in-process profile removal was incomplete "
                  + $"({state.OwnedProcessesAliveAtCompanionExit?.ToString() ?? "unknown"} owned child processes were still alive when it exited); "
                  + $"the exact profile path was removed after every owned process had exited; entries left: [{leftovers}]"
                : $"removed-leftovers: {left.Length} profile directories the companion did not remove (companion exit code "
                  + $"{state.CompanionExitCode?.ToString() ?? "unknown"}{(escalated ? ", after escalated termination" : "")}, owned processes remaining "
                  + $"{state.OwnedProcessesRemaining?.ToString() ?? "unknown"}); entries left: [{leftovers}]";
        }
        if (!outcome.StartsWith("failed", StringComparison.Ordinal)) {
            try { Directory.Delete(_runDirectory, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                outcome = $"failed: the run directory could not be removed ({exception.Message})";
            }
        }
        if (OperatingSystem.IsLinux() && Snapshot().ExitEscalated)
            outcome += "; the companion's /tmp/ifc-* IPC directory cannot be attributed after termination";
        Diagnostics.Cleanup(outcome);
        if (outcome.StartsWith("failed", StringComparison.Ordinal)) _logger.LogError("CEF run cleanup {Outcome} ({Run})", outcome, _runDirectory);
        else _logger.LogDebug("CEF run cleanup {Outcome}", outcome);
    }
}
