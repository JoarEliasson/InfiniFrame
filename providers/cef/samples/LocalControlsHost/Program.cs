using Contoso.InfiniFrame.CefHost;
using InfiniFrame;
using InfiniFrame.Application;
using InfiniFrame.BlazorWebView;
using InfiniFrame.Security;
using LocalControlsHost;
using LocalControlsUi.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

try {
    HostArguments arguments = HostArguments.Parse(args);
    if (arguments.Help) {
        Console.WriteLine("LocalControlsHost [--payload <verified-runtime-directory>] [--state-root <directory>]");
        Console.WriteLine("Use --payload for a shared installed full runtime. Without it, runtimes/<rid>/native/cef must be supplied externally beside the app; the managed package contains no runtime.");
        return 0;
    }

    var options = new CefHostOptions {
        // The provider creates and removes a unique run directory below this short temporary root.
        StateRoot = arguments.StateRoot ?? Path.Combine(Path.GetTempPath(), "lc-cef"),
        AllowInlineStyleAttributes = true,
        DeviceScaleFactor = 1,
        DisablePinchZoom = true,
        DisableOverscroll = true
    };
    if (arguments.PayloadDirectory is not null) options.PayloadDirectory = arguments.PayloadDirectory;
    var provider = new CefHostProvider(options);
    var builder = InfiniFrameApplication.CreateBuilder();
    builder.Services.AddLogging(logging => logging
        .AddSimpleConsole(console => console.SingleLine = true)
        .SetMinimumLevel(LogLevel.Information));
    builder.Services.AddSingleton<IErrorBoundaryLogger, HostErrorBoundaryLogger>();
    builder.Services.AddLocalControlsUi();
    builder.UseHostProvider(provider);
    builder.WithWindow("main", window => {
        window.SetTitle("Local controls example")
            .SetLocation(0, 0).SetSize(1024, 600)
            .SetChromeless(true).SetResizable(false)
            .SetStartPageUrl("app://localhost/");
        // Explicitly override permissive window-builder defaults for this local-only provider.
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
        window.AddTrustedOrigin("app://localhost/");
        window.RegisterWebMessagePostHandler("local-controls:error", (_, detail) =>
            Console.Error.WriteLine("Browser/Blazor error: " + detail));
    });
    builder.UseBlazorWebView(configuration => {
        configuration.RootComponents.Add<HostRoot>("#app");
        configuration.Configure(blazor => {
            blazor.AppBaseUri = new Uri("app://localhost/");
            blazor.HostPage = "index.html";
            // The framework's optional AppDomain handler opens a native message dialog.
            // This host uses console diagnostics and the in-document ErrorBoundary instead.
            blazor.EnableGlobalUnhandledExceptionHandler = false;
        });
    }, "main");

    // RunAsync uses the framework's cancellation, native-close and application-stop lifecycle.
    // DisposeAsync also disposes application services, stopping the RCL backend's timer and streams.
    await using var application = builder.Build();
    using var stopping = new CancellationTokenSource();
    ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stopping.Cancel(); };
    Console.CancelKeyPress += onCancel;
    Console.WriteLine("Starting LocalControlsUi.App through the Contoso CEF provider.");
    Console.WriteLine($"Payload: {options.PayloadDirectory}");
    Console.WriteLine($"State root: {options.StateRoot}");
    try { await application.RunAsync(stopping.Token); }
    catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
        Console.WriteLine("Local controls host stopped by cancellation.");
    }
    finally {
        Console.CancelKeyPress -= onCancel;
        Console.WriteLine("CEF provider shutdown diagnostics: " + JsonSerializer.Serialize(provider.GetDiagnostics()));
    }
    return 0;
}
catch (Exception error) {
    Console.Error.WriteLine($"Local controls host failed: {error}");
    return 1;
}
