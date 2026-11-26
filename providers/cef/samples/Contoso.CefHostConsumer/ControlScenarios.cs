using System.Diagnostics;
using System.Text.Json;
using Contoso.InfiniFrame.CefHost;
using InfiniFrame;
using InfiniFrame.Application;
using InfiniFrame.BlazorWebView;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Contoso.CefHostConsumer;

/// <summary>
///     Controls that separate InfiniFrame's own behaviour from provider behaviour, and what the application process loads.
/// </summary>
internal static class ControlScenarios {
    /// <summary>
    ///     A replaced page's late work alone: page 1 schedules a delayed render and a delayed JS interop call (1.5 s), the window
    ///     navigates to page 2, and the outcome is read from page 2. With <paramref name="defaultBackend" /> the same
    ///     Blazor app runs on InfiniFrame's default native backend (WebView2 on Windows), which does not use the hosted
    ///     provider contract at all, so a shared outcome belongs to InfiniFrame's Blazor integration, not to this provider.
    /// </summary>
    internal static async Task LateJsAsync(Checks checks, bool defaultBackend) {
        InfiniFrameApplicationBuilder builder = InfiniFrameApplication.CreateBuilder();
        builder.Services.AddLogging(logging => logging.AddSimpleConsole(options => options.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        var state = new ProbeState();
        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton<LifetimeProducer>();
        CefHostProvider? provider = null;
        if (!defaultBackend) builder.UseHostProvider(provider = new CefHostProvider(ConsumerHost.Options()));
        builder.WithWindow("main", window => ConsumerHost.Secure(window, "Late-work control", ConsumerHost.Origin));
        builder.UseBlazorWebView(configuration => configuration.RootComponents.Add<App>("app"), "main");
        using InfiniFrameApplication application = builder.Build();
        Task run = Start(application, defaultBackend);
        checks.Observe("backend", defaultBackend ? "default native backend (WebView2 on Windows)" : "external CEF provider");
        IInfiniFrameWindow main = await ConsumerHost.WindowAsync(application, "main");
        await main.Features.Lifecycle.WaitForReadyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60));
        checks.That("page 1 rendered", await Page.WaitFor(main, "document.querySelector('#assets')?.textContent === 'PASS'", 60000));
        string marker1 = await Page.Evaluate(main, "window.__documentMarker");
        await Page.Click(main, "#delayed-stamp");
        NavigationResult navigation = await main.Features.PageNavigation.LoadAsync(new Uri("app://localhost/screen?page=2"));
        bool page2 = await Page.WaitFor(main, "document.querySelector('#assets')?.textContent === 'PASS'");
        string marker2 = await Page.Evaluate(main, "window.__documentMarker");
        checks.That("navigation replaced the document", navigation.Status == NavigationStatus.Succeeded && page2 && marker2 != marker1,
            $"{navigation.Status}; markers {marker1[..Math.Min(8, marker1.Length)]} -> {marker2[..Math.Min(8, marker2.Length)]}");
        await Task.Delay(state.DelayedMilliseconds + 1500);
        JsonElement after = await Page.EvaluateJson(main,
            "(()=>({stamps: window.__probeStamps, lastStamp: document.documentElement.dataset.lastStamp ?? null, late: document.querySelector('#late-stamp').textContent, page: document.querySelector('#page-instance').textContent, marker: window.__documentMarker, location: location.href}))()");
        string? browserUrl = main.Features.PageNavigation.GetCurrentUrl();
        // Wait (bounded) for the outcome of page 1's late call on Blazor's dispatcher (refused, or its 2 s token).
        await Page.Until(() => state.DelayedOutcomes.Any(static outcome => outcome.StartsWith("js on dispatcher", StringComparison.Ordinal)), 5000);
        checks.Observe("page2-after-delay", after);
        checks.Observe("browser-current-url-after-delay", browserUrl);
        checks.Observe("delayed-outcomes", state.DelayedOutcomes.ToArray());
        checks.That("setup: page 1's delayed render and JS call both ran after page 1 was replaced",
            state.DelayedRenderAttempts == 1 && state.DelayedJsAttempts == 1, string.Join("; ", state.DelayedOutcomes));
        checks.That("page 1's late render did not reach page 2's DOM", after.GetProperty("late").GetString() == "none", after.ToString());
        checks.That("page 1's late JS interop call was not executed by page 2",
            after.GetProperty("stamps").GetArrayLength() == 0 && after.GetProperty("lastStamp").ValueKind == JsonValueKind.Null, after.ToString());
        checks.That("setup: page 1's late navigation and late JS call on Blazor's dispatcher both ran after page 1 was replaced",
            state.DelayedNavigationAttempts == 1 && state.DelayedDispatcherJsAttempts == 1, string.Join("; ", state.DelayedOutcomes));
        checks.That("page 1's late navigation did not move page 2's document (DOM location and browser URL unchanged)",
            after.GetProperty("location").GetString() == "app://localhost/screen?page=2" && after.GetProperty("marker").GetString() == marker2
            && browserUrl == "app://localhost/screen?page=2", $"{after}; browser {browserUrl}");
        if (!defaultBackend)
            checks.That("page 1's late JS call on Blazor's dispatcher was refused with JSDisconnectedException (not run, not answered by page 2)",
                state.DelayedOutcomes.Contains("js on dispatcher page 1: JSDisconnectedException"), string.Join("; ", state.DelayedOutcomes));
        await main.CloseAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(60));
        if (provider is not null) ConsumerHost.CheckRunCleanup(checks, provider.GetDiagnostics());
    }

    private static Task Start(InfiniFrameApplication application, bool defaultBackend) {
        if (!defaultBackend || !OperatingSystem.IsWindows()) return Task.Run(application.Run);
        // The default Windows backend runs its message loop on the calling thread, which must be STA.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try { application.Run(); completion.TrySetResult(); }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>
    ///     What the application process itself loaded while the CEF provider ran: managed InfiniFrame/provider assemblies
    ///     (with location), and whether any default-backend or browser-engine native module is mapped in this process.
    /// </summary>
    internal static void ObserveModules(Checks checks) {
        string[] managed = [.. AppDomain.CurrentDomain.GetAssemblies()
            .Select(static assembly => assembly.GetName().Name ?? "?")
            .Where(static name => name.StartsWith("InfiniFrame", StringComparison.Ordinal) || name.StartsWith("Contoso", StringComparison.Ordinal)
                || name.Contains("WebView", StringComparison.Ordinal))
            .Order()];
        string[] all;
        using (Process self = Process.GetCurrentProcess()) {
            all = [.. self.Modules.Cast<ProcessModule>().Select(static module => module.FileName).Distinct()];
        }
        // Observation: modules outside the OS's own folders (Windows directory; /usr and /lib on Linux).
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] native = [.. all.Where(path => OperatingSystem.IsWindows()
                ? windows.Length == 0 || !path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)
                : !path.StartsWith("/usr/", StringComparison.Ordinal) && !path.StartsWith("/lib", StringComparison.Ordinal))
            .Select(static path => Path.GetFileName(path)).Order()];
        checks.Observe("managed-assemblies-loaded", managed);
        checks.Observe("non-system-native-modules-loaded", native);
        checks.Observe("mapped-modules-total", all.Length);
        // The check covers EVERY mapped module (system folders included): no browser engine or default-backend library.
        string[] exact = ["InfiniFrame.Native.dll", "WebView2Loader.dll", "EmbeddedBrowserWebView.dll", "libcef.dll", "libcef.so", "libInfiniFrame.Native.so"];
        string[] prefixes = ["libwebkit2gtk", "libjavascriptcoregtk"];
        string[] found = [.. all.Select(static path => Path.GetFileName(path)).Where(name => exact.Contains(name, StringComparer.OrdinalIgnoreCase)
            || prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))];
        checks.That("no browser engine or default-backend native module is loaded in the application process (CEF runs only in the companion)",
            all.Length > 0 && found.Length == 0,
            found.Length == 0 ? $"none of {string.Join(", ", exact)} or {string.Join("*, ", prefixes)}* among {all.Length} mapped modules" : string.Join(", ", found));
        checks.Observe("managed-native-bridge-assembly-loaded", managed.Contains("InfiniFrame.NativeBridge"));
    }
}
