using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.Json;
using InfiniFrame.Hosting;
using Microsoft.Extensions.Logging;

namespace Contoso.InfiniFrame.CefHost.Tests.Infrastructure;

/// <summary>
///     Drives one provider/host pair the way InfiniFrame does: <c>CreateHost</c>, <c>StartAsync</c>,
///     <c>CreateWindowAsync</c> with a recording sink, window operations, <c>ShutdownAsync</c>, then <c>DisposeAsync</c>.
///     Every step is bounded. Disposal of the harness is a safety net that never throws: it disposes the host if the
///     test did not, terminates any process the test recorded that is still alive (exact id and creation time only),
///     writes the diagnostics, the provider log and the fake's log to the test output, and removes the test directory.
/// </summary>
internal sealed class HostHarness : IAsyncDisposable {
    private readonly ConcurrentQueue<ProcessIdentity> _tracked = new();
    private bool _hostDisposed;

    private HostHarness(TestDirectory directory, FakeCompanion? fake, CefHostOptions options, CefHostProvider provider) {
        Directory = directory;
        FakeOrNull = fake;
        Options = options;
        Provider = provider;
        Host = provider.CreateHost(new InfiniFrameHostCreateContext { ApplicationId = Guid.NewGuid() });
    }

    /// <summary>A host whose companion is <c>fake_companion.py</c> in <paramref name="mode" />.</summary>
    internal static HostHarness Create(string tag, string mode, IReadOnlyList<string>? fakeArguments = null, Action<CefHostOptions>? configure = null) =>
        Create(tag, mode, _ => fakeArguments ?? [], configure);

    /// <summary>As above, with fake arguments that refer to the test directory (for example a PID file).</summary>
    internal static HostHarness Create(string tag, string mode, Func<TestDirectory, IReadOnlyList<string>> fakeArguments, Action<CefHostOptions>? configure = null) {
        var directory = new TestDirectory(tag);
        var fake = new FakeCompanion(directory, mode, fakeArguments(directory));
        var options = new CefHostOptions {
            StateRoot = directory.StateRoot,
            CommandTimeout = TimeSpan.FromSeconds(30),
            ResourceTimeout = TimeSpan.FromSeconds(30),
            ExitTimeout = TimeSpan.FromSeconds(3)
        };
        configure?.Invoke(options);
        CefHostOptions snapshot = options.Snapshot();
        return new HostHarness(directory, fake, snapshot, new CefHostProvider(snapshot, fake.Launch));
    }

    /// <summary>A host on the real companion payload: the public constructor, payload validation, no test launch.</summary>
    internal static HostHarness CreateReal(string tag, string payloadDirectory, Action<CefHostOptions>? configure = null) {
        var directory = new TestDirectory(tag);
        var options = new CefHostOptions { PayloadDirectory = payloadDirectory, StateRoot = directory.StateRoot };
        configure?.Invoke(options);
        var provider = new CefHostProvider(options);
        return new HostHarness(directory, null, options.Snapshot(), provider);
    }

    internal TestDirectory Directory { get; }
    internal FakeCompanion? FakeOrNull { get; }
    internal FakeCompanion Fake => FakeOrNull ?? throw new InvalidOperationException("This harness runs the real companion.");
    internal CefHostOptions Options { get; }
    internal CefHostProvider Provider { get; }
    internal IInfiniFrameHost Host { get; }
    internal CefHost Cef => (CefHost)Host;
    internal CapturingLoggerFactory Logs { get; } = new();
    internal CefHostDiagnostics Diagnostics => Provider.GetDiagnostics() ?? throw new InvalidOperationException("No host.");
    internal ProcessIdentity? Companion { get; private set; }
    internal TimeSpan? DisposeDuration { get; private set; }

    internal InfiniFrameHostStartContext StartContext() => new() { Services = EmptyServices.Instance, LoggerFactory = Logs };

    internal async Task StartAsync() {
        try {
            await Wait.WithinAsync(Host.StartAsync(StartContext(), CancellationToken.None).AsTask(), "host.StartAsync").ConfigureAwait(false);
        }
        finally {
            CaptureCompanion();
        }
    }

    /// <summary>Records the companion's identity (id and creation time) so its exit can be verified exactly.</summary>
    internal void CaptureCompanion() {
        if (Companion is not null) return;
        CefConnection connection;
        try {
            connection = Cef.Connection;
        }
        catch (InvalidOperationException) {
            return;
        }
        Companion = connection.ProcessStartTime is {} started
            ? new ProcessIdentity(connection.ProcessId, started.ToUniversalTime())
            : ProcessIdentity.TryCapture(connection.ProcessId);
        Track(Companion);
    }

    /// <summary>Adds a process (for example a grandchild) that must be gone when the test ends.</summary>
    internal void Track(ProcessIdentity? identity) {
        if (identity is not null) _tracked.Enqueue(identity);
    }

    internal async Task<(CefWindowHost Window, RecordingSink Sink)> CreateWindowAsync(string title = "provider test window", bool waitForDocument = true) {
        var sink = new RecordingSink();
        IInfiniFrameWindowHost window = await Wait.WithinAsync(
            Host.CreateWindowAsync(Requests.Window(title), sink, CancellationToken.None).AsTask(), "host.CreateWindowAsync").ConfigureAwait(false);
        if (waitForDocument) await sink.WaitForAsync(nameof(RecordingSink.DocumentAttached), 1).ConfigureAwait(false);
        return ((CefWindowHost)window, sink);
    }

    /// <summary>As InfiniFrame does before shutdown: request the close, wait for Closed, then dispose the window host.</summary>
    internal static async Task CloseWindowAsync(CefWindowHost window, RecordingSink sink) {
        await Wait.WithinAsync(window.RequestCloseAsync(CancellationToken.None).AsTask(), "window.RequestCloseAsync").ConfigureAwait(false);
        await sink.WaitForAsync(nameof(RecordingSink.Closed), 1).ConfigureAwait(false);
        await Wait.WithinAsync(window.DisposeAsync().AsTask(), "window.DisposeAsync").ConfigureAwait(false);
    }

    internal async Task<TimeSpan> ShutdownAsync() {
        var clock = Stopwatch.StartNew();
        await Wait.WithinAsync(Host.ShutdownAsync(CancellationToken.None).AsTask(), "host.ShutdownAsync").ConfigureAwait(false);
        return clock.Elapsed;
    }

    /// <summary>
    ///     The provider's documented worst case for <c>DisposeAsync</c> (CefConnection.DisposeAsync and
    ///     CefHost.CleanupRunDirectoryAsync): 2 s to flush, the exit timeout, 10 s after escalation, the exit timeout for
    ///     descendants, 10 s after their termination, 10 s for the loops, plus profile-removal retries and a margin.
    /// </summary>
    internal TimeSpan DisposeBound => TimeSpan.FromSeconds(2) + Options.ExitTimeout + TimeSpan.FromSeconds(10) + Options.ExitTimeout
        + TimeSpan.FromSeconds(10) + TimeSpan.FromSeconds(10) + TimeSpan.FromSeconds(5);

    /// <summary>Disposes the host (as InfiniFrame does, exactly once) and returns how long it took. Bounded.</summary>
    internal async Task<TimeSpan> DisposeHostAsync() {
        if (_hostDisposed) throw new InvalidOperationException("The host was already disposed.");
        _hostDisposed = true;
        var clock = Stopwatch.StartNew();
        try {
            await Wait.WithinAsync(Host.DisposeAsync().AsTask(), "host.DisposeAsync (its documented bound)", DisposeBound).ConfigureAwait(false);
        }
        catch (TimeoutException) {
            KillLeftovers("host disposal exceeded its documented bound");
            throw;
        }
        DisposeDuration = clock.Elapsed;
        return clock.Elapsed;
    }

    /// <summary>Processes this test recorded that are still alive (exact identity), after a short grace period.</summary>
    internal async Task<IReadOnlyList<ProcessIdentity>> LeftoverProcessesAsync(TimeSpan? grace = null) {
        var clock = Stopwatch.StartNew();
        TimeSpan limit = grace ?? TimeSpan.FromSeconds(2);
        while (true) {
            ProcessIdentity[] alive = [.. _tracked.Where(static identity => identity.IsAlive())];
            if (alive.Length == 0 || clock.Elapsed >= limit) return alive;
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    private void KillLeftovers(string why) {
        foreach (ProcessIdentity identity in _tracked)
            if (identity.KillIfAlive())
                Console.WriteLine($"[harness] EMERGENCY CLEANUP ({why}): terminated leftover {identity}");
    }

    public async ValueTask DisposeAsync() {
        try {
            if (!_hostDisposed) {
                try {
                    await DisposeHostAsync().ConfigureAwait(false);
                }
                catch (Exception exception) {
                    Console.WriteLine($"[harness] host disposal failed in the safety net: {exception.GetType().Name}: {exception.Message}");
                }
            }
        }
        finally {
            KillLeftovers("safety net at the end of the test");
            Dump();
            Directory.Dispose();
            if (Directory.RemovalFailure is {} failure) Console.WriteLine($"[harness] test directory left behind: {failure}");
        }
    }

    private void Dump() {
        var builder = new StringBuilder();
        builder.AppendLine($"[harness] disposal took {DisposeDuration?.TotalMilliseconds.ToString("0") ?? "n/a"} ms (bound {DisposeBound.TotalSeconds:0} s); companion {Companion?.ToString() ?? "not started"}");
        try {
            builder.AppendLine("[harness] diagnostics:");
            builder.AppendLine(JsonSerializer.Serialize(Diagnostics, DumpOptions));
        }
        catch (Exception exception) {
            builder.AppendLine($"[harness] diagnostics unavailable: {exception.Message}");
        }
        builder.AppendLine("[harness] provider log:");
        foreach (string line in Logs.Lines.TakeLast(200)) builder.Append("  ").AppendLine(line.Length > 600 ? line[..600] + "…" : line);
        if (FakeOrNull is {} fake) {
            builder.AppendLine("[harness] fake companion log:");
            builder.Append(fake.Render());
        }
        Console.WriteLine(builder.ToString());
    }

    private static readonly JsonSerializerOptions DumpOptions = new() { WriteIndented = true };
}

/// <summary>A valid window request: everything <see cref="CefHost.Validate" /> requires.</summary>
internal static class Requests {
    internal const string StartUrl = "app://localhost/index.html";

    internal static InfiniFrameHostWindowRequest Window(string title = "provider test window", bool chromeless = false,
        bool resizable = true, Size? minimumSize = null, Size? maximumSize = null, Size? size = null, bool providerDefaultSize = false) => new() {
        WindowId = Guid.NewGuid(),
        StartUrl = new Uri(StartUrl),
        CustomSchemes = ["app"],
        TrustedOrigins = [new Uri("app://localhost/")],
        TrustAllOrigins = false,
        BridgeScript = "(() => { /* stand-in for InfiniFrame's generated bridge, contract version 1 */ })();",
        BridgeContractVersion = InfiniFrameHostContract.BridgeContractVersion,
        Title = title,
        InitialLocation = new Point(120, 80),
        InitialSize = providerDefaultSize ? null : size ?? new Size(900, 700),
        Chromeless = chromeless,
        Resizable = resizable,
        MinimumSize = minimumSize,
        MaximumSize = maximumSize,
        InitialState = InfiniFrameHostWindowState.Normal,
        BrowserPolicy = new InfiniFrameHostBrowserPolicy {
            WebSecurity = true,
            DevTools = false,
            ContextMenu = false,
            GrantBrowserPermissions = false,
            JavaScriptClipboardAccess = false,
            MediaStream = false,
            MediaAutoplay = false,
            FileSystemAccess = false,
            IgnoreCertificateErrors = false,
            UserAgent = null
        }
    };
}

/// <summary>Sends a <c>fake:</c> directive through set-title; the fake performs it before replying.</summary>
internal static class FakeDirectives {
    internal static Task DirectAsync(this CefWindowHost window, string directive) =>
        Wait.WithinAsync(window.SetTitleAsync("fake:" + directive, CancellationToken.None).AsTask(), $"directive '{directive}'");
}

internal sealed class EmptyServices : IServiceProvider {
    internal static readonly EmptyServices Instance = new();
    public object? GetService(Type serviceType) => null;
}

/// <summary>Captures the provider's log (the application's logger factory, as InfiniFrame passes it).</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory {
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    internal IReadOnlyList<string> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);
    public void AddProvider(ILoggerProvider provider) {}
    public void Dispose() {}

    private sealed class CapturingLogger(CapturingLoggerFactory owner) : ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
            string line = $"+{owner._clock.ElapsedMilliseconds,6} ms {logLevel,-11} {formatter(state, exception)}";
            if (exception is not null) line += $" | {exception.GetType().Name}: {exception.Message}";
            owner._lines.Enqueue(line);
        }
    }
}
