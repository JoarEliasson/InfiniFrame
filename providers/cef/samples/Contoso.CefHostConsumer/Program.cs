using System.Diagnostics;
using System.Text.Json;
using Contoso.InfiniFrame.CefHost;
using InfiniFrame;
using InfiniFrame.Application;
using InfiniFrame.BlazorWebView;
using InfiniFrame.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Contoso.CefHostConsumer;

// =====================================================================================================================
// Package-only consumer: InfiniFrame and the external Contoso.InfiniFrame.CefHost provider, both through
// PackageReference. REAL BROWSER RUN: the CEF companion renders the page and runs its scripts; every check drives or
// reads the page through InfiniFrame's public APIs (script evaluation travels the same bridge).
// =====================================================================================================================
public static class Program {
    public static async Task<int> Main(string[] args) {
        string scenario = args.Length > 0 ? args[0] : "main";
        string report = args.Length > 1 ? args[1] : Path.Join(AppContext.BaseDirectory, $"report-{scenario}.json");
        var checks = new Checks(scenario);
        Console.WriteLine($"CONSUMER scenario={scenario} pid={Environment.ProcessId}");
        try {
            await (scenario switch {
                "main" => MainScenario.RunAsync(checks),
                "renderer-loss" => FaultScenarios.RendererLossAsync(checks),
                "companion-loss" => FaultScenarios.CompanionLossAsync(checks),
                "send-failure" => FaultScenarios.ActiveDocumentSendFailureAsync(checks),
                "close-during-creation" => LifecycleScenarios.CloseDuringCreationAsync(checks),
                "shutdown-during-creation" => LifecycleScenarios.ShutdownDuringCreationAsync(checks),
                "rejected-close" => LifecycleScenarios.RejectedAndDuplicateCloseAsync(checks),
                "native-close" => LifecycleScenarios.NativeCloseAsync(checks),
                "idle" => LifecycleScenarios.IdleAsync(checks),
                "refusals" => LifecycleScenarios.RefusalsAsync(checks),
                "eval-contract" => EvalContractAsync(checks, defaultBackend: false),
                "eval-contract-default" => EvalContractAsync(checks, defaultBackend: true),
                "late-work-cef" => ControlScenarios.LateJsAsync(checks, defaultBackend: false),
                "late-work-default" => ControlScenarios.LateJsAsync(checks, defaultBackend: true),
                _ => throw new ArgumentException($"Unknown scenario '{scenario}'.")
            }).WaitAsync(TimeSpan.FromMinutes(6));
        }
        catch (Exception exception) {
            checks.That("scenario completed", false, exception.ToString());
        }
        return checks.Finish(report);
    }

    /// <summary>
    ///     Records the raw ExecuteJavaScriptAsync result text for several value types through the real bridge. With
    ///     defaultBackend, the same page runs on InfiniFrame's default native backend (WebView2 here) as the control.
    /// </summary>
    private static async Task EvalContractAsync(Checks checks, bool defaultBackend) {
        InfiniFrameApplicationBuilder builder = InfiniFrameApplication.CreateBuilder();
        builder.Services.AddLogging(logging => logging.AddSimpleConsole(options => options.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        CefHostProvider? provider = defaultBackend ? null : new CefHostProvider(ConsumerHost.Options());
        if (provider is not null) builder.UseHostProvider(provider);
        builder.WithWindow("main", window => {
            ConsumerHost.Secure(window, "eval contract", ConsumerHost.Origin + "secondary.html");
            window.RegisterCustomSchemeHandler("app", StaticAssets.Handle);
        });
        using InfiniFrameApplication application = builder.Build();
        Task run;
        if (defaultBackend && OperatingSystem.IsWindows()) {
            // The default Windows backend runs its message loop on the calling thread, which must be STA.
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => {
                try { application.Run(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            run = completion.Task;
        }
        else run = Task.Run(application.Run);
        IInfiniFrameWindow main = await ConsumerHost.WindowAsync(application, "main");
        await main.Features.Lifecycle.WaitForReadyAsync();
        checks.That("page loaded", await Page.WaitFor(main, ConsumerHost.StaticPageRendered, 60000));
        checks.Observe("backend", defaultBackend ? "default native backend" : "external CEF provider");
        foreach (string expression in new[] { "true", "42", "'abc'", "({a:1,b:'x'})", "JSON.stringify({a:1})", "[1,2]", "null", "undefined", "Promise.resolve(1)",
                     "document.title", "document.querySelector('title') && document.querySelector('title').textContent", "document.head.children.length",
                     "document.visibilityState", "document.hasFocus()", "document.documentElement.outerHTML.slice(0, 600)", "document.contentType",
                     "document.characterSet", "document.compatMode" }) {
            string? raw = await main.Features.JavaScript.ExecuteJavaScriptAsync(expression);
            checks.That($"raw result recorded for {expression}", true, raw ?? "(C# null)");
        }
        try {
            string? typed = await main.Features.JavaScript.ExecuteJavaScriptAsync<string>("'typed'");
            checks.That("typed string result recorded", true, typed is null ? "(null)" : $"{typed} (length {typed.Length})");
        }
        catch (Exception exception) { checks.That("typed string result recorded", true, $"threw {exception.GetType().Name}: {exception.Message}"); }
        try {
            int typedNumber = await main.Features.JavaScript.ExecuteJavaScriptAsync<int>("42");
            checks.That("typed number result recorded", true, typedNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception exception) { checks.That("typed number result recorded", true, $"threw {exception.GetType().Name}: {exception.Message}"); }
        await main.CloseAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(60));
        if (provider is not null) {
            checks.Observe("provider-diagnostics", provider.GetDiagnostics());
            checks.Observe("stale-runs-reclaimed-at-start", provider.GetDiagnostics()?.StaleRunsReclaimed);
            ConsumerHost.CheckRunCleanup(checks, provider.GetDiagnostics());
        }
    }
}

internal static partial class ProgramScenarios {}

/// <summary>Records named checks and writes a JSON report. Every check line is also printed.</summary>
internal sealed class Checks(string scenario) {
    private readonly List<object> _results = [];
    private readonly Dictionary<string, object?> _observations = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    internal int Failures { get; private set; }
    internal long ElapsedMs => _clock.ElapsedMilliseconds;

    internal void That(string name, bool pass, object? detail = null) {
        lock (_results) {
            _results.Add(new { check = name, pass, atMs = _clock.ElapsedMilliseconds, detail = detail?.ToString() });
            if (!pass) Failures++;
        }
        Console.WriteLine($"CHECK {(pass ? "PASS" : "FAIL")} [{scenario}] {name}{(detail is null ? "" : $" :: {Shorten(detail.ToString()!)}")}");
    }

    internal void Observe(string name, object? value) {
        lock (_results) _observations[name] = value;
        Console.WriteLine($"OBSERVE [{scenario}] {name} = {Shorten(JsonSerializer.Serialize(value))}");
    }

    /// <summary>Marks a point the external driver synchronises on (process/memory sampling, fault injection).</summary>
    internal static void Marker(string name) => Console.WriteLine($"MARKER {name}");

    internal static string Shorten(string text) => text.Length <= 400 ? text : text[..400] + "…";

    internal int Finish(string path) {
        var report = new {
            scenario, label = "real CEF browser through the public host contract (package-only consumer)",
            runtime = Environment.Version.ToString(), os = Environment.OSVersion.ToString(), elapsedMs = _clock.ElapsedMilliseconds,
            failures = Failures, results = _results, observations = _observations
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"CONSUMER done scenario={scenario} checks={_results.Count} failures={Failures} report={path}");
        return Failures == 0 ? 0 : 1;
    }
}

/// <summary>Drives and reads the real page through InfiniFrame's public JavaScript API.</summary>
internal static class Page {
    /// <summary>
    ///     Decodes InfiniFrame's script result as observed through the real bridge: the raw text is a JSON string whose
    ///     content is the JSON of the value (for example "\"abc\"" for 'abc', "\"true\"" for true). If a future
    ///     contract returns the value's JSON directly, that is accepted too.
    /// </summary>
    internal static JsonElement Decode(string? raw) {
        using JsonDocument outer = JsonDocument.Parse(raw ?? "null");
        if (outer.RootElement.ValueKind != JsonValueKind.String) return outer.RootElement.Clone();
        string inner = outer.RootElement.GetString()!;
        try {
            using JsonDocument value = JsonDocument.Parse(inner);
            return value.RootElement.Clone();
        }
        catch (JsonException) {
            return outer.RootElement.Clone();
        }
    }

    internal static async Task<string> Evaluate(IInfiniFrameWindow window, string expression, CancellationToken cancellationToken = default) {
        string? raw = await window.Features.JavaScript.ExecuteJavaScriptAsync(expression, cancellationToken);
        if (raw is null) return "";
        JsonElement value = Decode(raw);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
    }

    /// <summary>Evaluates to a JSON structure (an object or array value, or a JSON text produced with JSON.stringify).</summary>
    internal static async Task<JsonElement> EvaluateJson(IInfiniFrameWindow window, string expression) {
        JsonElement value = Decode(await window.Features.JavaScript.ExecuteJavaScriptAsync(expression));
        if (value.ValueKind != JsonValueKind.String) return value;
        using JsonDocument text = JsonDocument.Parse(value.GetString()!);
        return text.RootElement.Clone();
    }

    /// <summary>Runs an async page expression: InfiniFrame's evaluation does not await promises, so the result is parked in a page global and polled.</summary>
    internal static async Task<JsonElement> EvaluateAsyncJson(IInfiniFrameWindow window, string asyncExpression, int timeoutMs = 30000) {
        string slot = "__probeAsync" + Guid.NewGuid().ToString("N");
        await Evaluate(window, $"(()=>{{window.{slot}=undefined;Promise.resolve({asyncExpression}).then(r=>{{window.{slot}={{ok:true,value:r}}}},e=>{{window.{slot}={{ok:false,error:String(e)}}}});return true}})()");
        if (!await WaitFor(window, $"window.{slot} !== undefined", timeoutMs)) throw new TimeoutException($"Async page expression did not settle: {asyncExpression}");
        JsonElement result = await EvaluateJson(window, $"JSON.stringify(window.{slot})");
        if (!result.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(result.GetProperty("error").GetString());
        return result.GetProperty("value").Clone();
    }

    internal static async Task<bool> WaitFor(IInfiniFrameWindow window, string expression, int timeoutMs = 30000) {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs) {
            try {
                if (await Evaluate(window, expression) == "true") return true;
            }
            catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or ObjectDisposedException) {
                // Document replacement in progress: the evaluation's document retired. Retry on the next document.
            }
            await Task.Delay(100);
        }
        return false;
    }

    internal static async Task<bool> Until(Func<bool> condition, int timeoutMs = 10000) {
        var clock = Stopwatch.StartNew();
        while (!condition()) {
            if (clock.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(25);
        }
        return true;
    }

    internal static async Task Click(IInfiniFrameWindow window, string selector) =>
        await Evaluate(window, $"(()=>{{document.querySelector('{selector}').click();return true}})()");
}

/// <summary>Builds the consumer application through public APIs only.</summary>
internal static class ConsumerHost {
    internal const string Origin = "app://localhost/";

    /// <summary>
    ///     The static page rendered as an HTML document (not merely its text): a real h1 element, the head's title, the
    ///     HTML media type and the UTF-8 charset declared by the scheme handler. A media type the browser does not
    ///     recognise is shown by Chromium as escaped plain text, which still contains the words but has none of these.
    /// </summary>
    internal const string StaticPageRendered = "document.querySelector('h1')?.textContent === 'Secondary window' && document.title === 'Isolated secondary view' "
        + "&& document.contentType === 'text/html' && document.characterSet === 'UTF-8'";

    internal static CefHostOptions Options() {
        var options = new CefHostOptions();
        if (Environment.GetEnvironmentVariable("CONSUMER_STATE_ROOT") is { Length: > 0 } root) options.StateRoot = root;
        return options;
    }

    /// <summary>The window configuration the provider requires: every permissive builder default turned off explicitly.</summary>
    internal static void Secure(IInfiniFrameWindowBuilder window, string title, string url) {
        window.SetTitle(title).SetLocation(100, 100).SetSize(900, 700).SetStartPageUrl(url);
        window.Features.Browser.EnableWebSecurity(true);
        window.Features.Browser.EnableIgnoreCertificateErrors(false);
        window.Features.Browser.EnableBrowserPermissions(false);
        window.Features.Browser.EnableFileSystemAccess(false);
        window.Features.Browser.EnableJavascriptClipboardAccess(false);
        window.Features.Browser.EnableMediaStream(false);
        window.Features.Browser.EnableMediaAutoplay(false);
        window.Features.Browser.EnableContextMenu(false);
        window.Features.Browser.SetUserAgent("");
        window.Debugging.EnableDevTools(false);
        window.SetTrustAllOrigins(false);
        window.AddTrustedOrigin(Origin);
    }

    internal sealed record Built(InfiniFrameApplication Application, CefHostProvider Provider, ProbeState State);

    internal static Built Build(bool blazor = true, bool secondary = false, Action<IInfiniFrameWindowBuilder>? main = null,
        Action<InfiniFrameApplicationBuilder>? extra = null) {
        InfiniFrameApplicationBuilder builder = InfiniFrameApplication.CreateBuilder();
        builder.Services.AddLogging(logging => logging.AddSimpleConsole(options => options.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        var state = new ProbeState();
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton<LifetimeProducer>();
        var provider = new CefHostProvider(Options());
        builder.UseHostProvider(provider);
        builder.WithWindow("main", window => {
            Secure(window, "InfiniFrame CEF provider sample", blazor ? Origin : Origin + "secondary.html");
            if (!blazor) window.RegisterCustomSchemeHandler("app", StaticAssets.Handle);
            main?.Invoke(window);
        });
        if (secondary)
            builder.WithWindow("secondary", window => {
                Secure(window, "InfiniFrame CEF isolation probe", Origin + "secondary.html");
                window.SetLocation(160, 160).SetSize(640, 480);
                window.RegisterCustomSchemeHandler("app", StaticAssets.Handle);
                window.RegisterWebMessageReceivedHandler((_, message) => {
                    if (message == "__bwv:secondary-isolation") Interlocked.Increment(ref state.SecondaryMessages);
                });
                window.RegisterWindowClosedHandler(_ => Interlocked.Increment(ref state.SecondaryClosed));
            });
        if (blazor) {
            builder.UseBlazorWebView(configuration => configuration.RootComponents.Add<App>("app"), "main");
            // Wrap Blazor's static file provider (public DI only) to inject failures through the real resource path.
            ServiceDescriptor original = builder.Services.Last(static descriptor => descriptor.ServiceType == typeof(IFileProvider));
            var inner = (IFileProvider)original.ImplementationInstance!;
            builder.Services.Remove(original);
            builder.Services.AddSingleton<IFileProvider>(new FaultFileProvider(inner, state));
        }
        extra?.Invoke(builder);
        return new Built(builder.Build(), provider, state);
    }

    internal static async Task<IInfiniFrameWindow> WindowAsync(InfiniFrameApplication application, string id, int timeoutMs = 30000) {
        var clock = Stopwatch.StartNew();
        IInfiniFrameWindow? window;
        while ((window = application.TryGetWindow(id)) is null) {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Window '{id}' was not registered.");
            await Task.Delay(20);
        }
        return window;
    }

    internal static string Diagnostics(CefHostProvider provider) =>
        JsonSerializer.Serialize(provider.GetDiagnostics(), new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    ///     End-of-run cleanup: every owned process (the companion and CEF's children, tracked by the provider's job) is
    ///     gone, and the per-run profile was removed at its exact path, either by the companion itself ("clean") or by the
    ///     provider after all owned processes had exited ("removed-after-exit"). Which of the two happened is reported.
    /// </summary>
    internal static void CheckRunCleanup(Checks checks, CefHostDiagnostics? diagnostics, bool companionKilled = false) {
        string cleanup = diagnostics?.Cleanup ?? "no diagnostics";
        checks.Observe("cleanup", new {
            cleanup, tracking = diagnostics?.ProcessTracking, ownedStarted = diagnostics?.OwnedProcessesStarted,
            aliveAtCompanionExit = diagnostics?.OwnedProcessesAliveAtCompanionExit, remaining = diagnostics?.OwnedProcessesRemaining,
            descendantsEscalated = diagnostics?.DescendantsEscalated, peakTreeCommitBytes = diagnostics?.PeakTreeCommitBytes
        });
        bool profileGone = companionKilled
            ? cleanup.StartsWith("removed-leftovers", StringComparison.Ordinal) || cleanup.StartsWith("clean", StringComparison.Ordinal)
            : cleanup.StartsWith("clean", StringComparison.Ordinal) || cleanup.StartsWith("removed-after-exit", StringComparison.Ordinal);
        checks.That("per-run profile removed at its exact path and the run directory is gone",
            profileGone && diagnostics?.RunDirectory is { } run && !Directory.Exists(run), cleanup);
        bool jobTracked = OperatingSystem.IsWindows();
        checks.That(jobTracked ? "every owned process (companion and CEF children) exited; none had to be terminated"
                : "companion exited without escalation (no job objects on this platform: the scenario driver verifies the CEF tree is gone)",
            jobTracked
                ? diagnostics is { ProcessTracking: "job object", OwnedProcessesRemaining: 0, DescendantsEscalated: false } && (companionKilled || !diagnostics.ExitEscalated)
                : diagnostics is { CompanionExitCode: not null } && (companionKilled || !diagnostics.ExitEscalated),
            $"tracking {diagnostics?.ProcessTracking}; started {diagnostics?.OwnedProcessesStarted}, alive at companion exit "
            + $"{diagnostics?.OwnedProcessesAliveAtCompanionExit}, remaining {diagnostics?.OwnedProcessesRemaining}, descendants escalated "
            + $"{diagnostics?.DescendantsEscalated}, companion escalated {diagnostics?.ExitEscalated}");
    }
}

/// <summary>Static files for windows without Blazor (served from the application's wwwroot).</summary>
internal static class StaticAssets {
    internal static (Stream? Data, string? ContentType) Handle(IInfiniFrameWindow window, string url) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Host != "localhost") return (null, null);
        string name = uri.AbsolutePath.TrimStart('/');
        if (name is not ("secondary.html" or "app.css")) return (null, null);
        string path = Path.Combine(AppContext.BaseDirectory, "wwwroot", name);
        return File.Exists(path) ? (File.OpenRead(path), name.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "text/html; charset=utf-8") : (null, null);
    }
}
