using System.Runtime.InteropServices;

namespace Contoso.InfiniFrame.CefHost;

/// <summary>Options for the CEF companion host provider.</summary>
public sealed class CefHostOptions {
    internal const string Origin = "app://localhost/";
    internal const string CefVersion = "156.0.8+g426920c+chromium-156.0.8078.48";
    internal const int ApiVersion = 15600;

    /// <summary>
    ///     The installed, RID-specific directory containing the companion and its complete CEF payload.
    ///     The managed package does not supply it. Defaults to an externally supplied app-local runtime directory;
    ///     callers using a shared installation must select its version folder explicitly.
    /// </summary>
    public string PayloadDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "runtimes", CurrentRid, "native", "cef");

    /// <summary>
    ///     Base directory for per-run state. Each host run gets its own new subdirectory, used as the companion's
    ///     profile location (LOCALAPPDATA on Windows, XDG_STATE_HOME on Linux) and removed after the companion exits.
    ///     Nothing outside that run directory is ever deleted. Default: a folder under the user's temporary directory.
    /// </summary>
    public string StateRoot { get; set; } = Path.Combine(Path.GetTempPath(), "Contoso.InfiniFrame.CefHost");

    /// <summary>
    ///     How long one companion command may stay unanswered (default 30 seconds). Delivery of an unanswered command is
    ///     uncertain and it is never replayed, so expiry faults the host.
    /// </summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long one resource request may take before it is answered with 504 (default 30 seconds).</summary>
    public TimeSpan ResourceTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How long disposal waits for the companion to exit after its input closed (default 10 seconds). After that the
    ///     provider terminates only the companion process tree it started, and reports the escalation.
    /// </summary>
    public TimeSpan ExitTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Allows inline <c>style</c> attributes in application documents (default <see langword="false" />: the strict
    ///     policy). When enabled, the companion's Content-Security-Policy adds only <c>style-src-attr 'unsafe-inline'</c>,
    ///     so markup such as generated Razor <c>style="…"</c> attributes, including values set through
    ///     <c>setAttribute</c>, applies. <c>&lt;style&gt;</c> elements and stylesheets from other origins stay blocked by
    ///     <c>default-src 'self'</c>. A page cannot relax the policy itself: a second policy in a <c>&lt;meta&gt;</c> element
    ///     only narrows it.
    /// </summary>
    public bool AllowInlineStyleAttributes { get; set; }

    /// <summary>
    ///     A fixed device scale factor for every window, between 0.5 and 4 (default <see langword="null" />: the
    ///     platform's scale factor). Use 1 for pixel-exact rendering on a panel whose layout is authored in device pixels.
    /// </summary>
    public double? DeviceScaleFactor { get; set; }

    /// <summary>Disables pinch zoom from touch screens and touchpads (default <see langword="false" />).</summary>
    public bool DisablePinchZoom { get; set; }

    /// <summary>
    ///     Disables elastic overscroll (the rubber-band effect at a scroll boundary) and overscroll history navigation
    ///     (default <see langword="false" />).
    /// </summary>
    public bool DisableOverscroll { get; set; }

    internal static string CurrentRid {
        get {
            string os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux"
                : throw new PlatformNotSupportedException("The CEF companion is available only for Windows and Linux.");
            string arch = RuntimeInformation.ProcessArchitecture switch {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                _ => throw new PlatformNotSupportedException("The CEF companion requires a native x64 or ARM64 process.")
            };
            return $"{os}-{arch}";
        }
    }

    internal CefHostOptions Snapshot() {
        Check(CommandTimeout, nameof(CommandTimeout), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        Check(ResourceTimeout, nameof(ResourceTimeout), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        Check(ExitTimeout, nameof(ExitTimeout), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
        if (string.IsNullOrWhiteSpace(PayloadDirectory)) throw new ArgumentException("PayloadDirectory is required.");
        if (string.IsNullOrWhiteSpace(StateRoot) || !Path.IsPathFullyQualified(StateRoot))
            throw new ArgumentException("StateRoot must be an absolute path.");
        if (DeviceScaleFactor is {} scale && !(scale >= MinimumDeviceScaleFactor && scale <= MaximumDeviceScaleFactor))
            throw new ArgumentOutOfRangeException(nameof(DeviceScaleFactor), scale,
                $"DeviceScaleFactor must be between {MinimumDeviceScaleFactor} and {MaximumDeviceScaleFactor}, or null for the platform's scale factor.");
        return new CefHostOptions {
            PayloadDirectory = PayloadDirectory, StateRoot = StateRoot, CommandTimeout = CommandTimeout,
            ResourceTimeout = ResourceTimeout, ExitTimeout = ExitTimeout, AllowInlineStyleAttributes = AllowInlineStyleAttributes,
            DeviceScaleFactor = DeviceScaleFactor, DisablePinchZoom = DisablePinchZoom, DisableOverscroll = DisableOverscroll
        };
    }

    internal const double MinimumDeviceScaleFactor = 0.5, MaximumDeviceScaleFactor = 4.0;

    /// <summary>The protocol-3 Hello host option flags (mirrors <c>kHost*</c> in the companion's protocol.h).</summary>
    internal uint HostOptionFlags =>
        (AllowInlineStyleAttributes ? 1u : 0u) | (DisablePinchZoom ? 2u : 0u) | (DisableOverscroll ? 4u : 0u);

    private static void Check(TimeSpan value, string name, TimeSpan minimum, TimeSpan maximum) {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be between {minimum} and {maximum}.");
    }
}
