using InfiniFrame.Hosting;

namespace Contoso.InfiniFrame.CefHost;

/// <summary>
///     An external InfiniFrame host provider backed by the CEF companion process. Select it with
///     <c>InfiniFrameApplicationBuilder.UseHostProvider(new CefHostProvider(options))</c> before <c>Build()</c>.
/// </summary>
/// <remarks>
///     InfiniFrame keeps the window facade, its lifecycle, managed dispatch and the Blazor integration; this provider
///     supplies native windows, the browser and their events through the public host contract only.
///     Implemented capabilities: bounds, window state (minimize, maximize, full screen, restore), focus, title, icon,
///     and (host contract 1.1) chromeless windows, fixed-size windows and minimum/maximum sizes. Sizes are outer window
///     sizes; a chromeless window's outer size is its content area. Not implemented: bounds/state/focus notifications
///     and native close cancellation (a native close closes the window). Only <c>app://localhost/</c> is served and
///     trusted.
/// </remarks>
public sealed class CefHostProvider : IInfiniFrameHostProvider {
    private readonly CefHostOptions _options;
    private readonly Func<string, CefLaunch>? _testLaunch;
    private CefHost? _host;

    /// <summary>Creates the provider. Options are copied and validated now.</summary>
    public CefHostProvider(CefHostOptions? options = null) : this((options ?? new CefHostOptions()).Snapshot(), null) {}

    internal CefHostProvider(CefHostOptions options, Func<string, CefLaunch>? testLaunch) {
        _options = options;
        _testLaunch = testLaunch;
    }

    /// <inheritdoc />
    public string DisplayName => "CEF companion (Contoso.InfiniFrame.CefHost)";

    /// <inheritdoc />
    public Version ContractVersion { get; } = new(InfiniFrameHostContract.MajorVersion, InfiniFrameHostContract.MinorVersion);

    /// <inheritdoc />
    public InfiniFrameHostCapabilities Capabilities =>
        InfiniFrameHostCapabilities.Bounds | InfiniFrameHostCapabilities.WindowState | InfiniFrameHostCapabilities.Focus
        | InfiniFrameHostCapabilities.Title | InfiniFrameHostCapabilities.Icon
        // Host contract 1.1: applied by the companion's window delegate at creation.
        | InfiniFrameHostCapabilities.Chromeless | InfiniFrameHostCapabilities.Resizable | InfiniFrameHostCapabilities.SizeConstraints;

    /// <inheritdoc />
    public IInfiniFrameHost CreateHost(InfiniFrameHostCreateContext context) {
        ArgumentNullException.ThrowIfNull(context);
        if (_host is not null) throw new InvalidOperationException("A CefHostProvider instance creates one host.");
        return _host = new CefHost(_options, context.ApplicationId, _testLaunch);
    }

    /// <summary>A diagnostics snapshot of this provider's host, or <see langword="null" /> before <c>Build()</c>.</summary>
    public CefHostDiagnostics? GetDiagnostics() => _host?.Snapshot();
}
