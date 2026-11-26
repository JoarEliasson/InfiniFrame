using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using InfiniFrame;
using InfiniFrame.Application;
using InfiniFrame.BlazorWebView;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Contoso.CoreGapRepro;

// Usage: Contoso.CoreGapRepro [gap|control|crosstalk] [report.json]
//   gap       page 1's retired IJSRuntime / NavigationManager / component act after document 2's page attached.
//   control   page 2's own services make the same calls (they belong to document 2).
//   crosstalk gap, plus: page 2 has its own JS call pending and the page answers probe.stamp as a real page would.
public static class Program {
    private const string AttachPage = "__bwv:[\"AttachPage\",\"app://localhost/\",\"app://localhost/\"]";
    private static readonly Uri Origin = new("app://localhost/");

    public static async Task<int> Main(string[] args) {
        string mode = args.Length > 0 ? args[0] : "gap";
        string path = args.Length > 1 ? args[1] : Path.Join(AppContext.BaseDirectory, $"repro-result-{mode}.json");
        var report = new Report(mode);
        try {
            if (mode is not ("gap" or "control" or "crosstalk")) throw new ArgumentException($"Unknown mode '{mode}' (gap | control | crosstalk).");
            await RunAsync(mode, report).WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (Exception exception) {
            report.Fail(exception);
        }
        return report.Finish(path);
    }

    private static async Task RunAsync(string mode, Report report) {
        var provider = new FakeHostProvider();
        InfiniFrameApplicationBuilder builder = InfiniFrameApplication.CreateBuilder();
        builder.Services.AddLogging(logging => logging.AddProvider(report.Logs).SetMinimumLevel(LogLevel.Debug));
        InfiniFrameApplication application = builder
            .UseHostProvider(provider)
            .WithWindow("main", static _ => {})
            .UseBlazorWebView(static blazor => blazor.RootComponents.Add<ProbePage>("#app"), "main")
            .Build();
        Task run = Task.Run(application.Run);
        FakeWindowHost host = await provider.Host!.Window.Task.WaitAsync(TimeSpan.FromSeconds(10));
        IInfiniFrameWindow window = application.GetWindow("main");
        await window.Features.Lifecycle.WaitForReadyAsync();
        host.AnswerStampCalls = mode == "crosstalk";

        // Step 1: document 1 attaches, its page attaches (AttachPage) and page 1 renders into it.
        await host.AttachAsync(1, Origin);
        await host.PostAsync(1, AttachPage);
        await Until(() => host.Delivered(1, "RenderBatch") > 0 && ProbePage.Count == 1, "page 1 rendered into document 1");
        ProbePage page1 = ProbePage.Get(1);
        report.Line("STEP", "1 done: page 1 rendered into document 1", $"last send #{host.LastSeq}");

        // Step 2: document 1 retires, document 2 attaches, document 2's page attaches and page 2 renders into it.
        await host.RetireAsync(1);
        await host.AttachAsync(2, Origin);
        await host.PostAsync(2, AttachPage);
        await Until(() => host.Delivered(2, "RenderBatch") > 0 && ProbePage.Count == 2, "page 2 rendered into document 2");
        ProbePage page2 = ProbePage.Get(2);
        await page2.OnDispatcher(static () => {});// settle: Blazor's dispatcher, the provider loop (acks), then a short quiet period
        await provider.Host.OnLoop(static () => {});
        await Task.Delay(200);
        int mark = host.LastSeq;
        report.Line("STEP", "2 done: document 1 retired, document 2 attached, page 2 rendered into document 2", $"last send #{mark}");
        report.Line("OBSERVE", "page 1 and page 2 hold distinct scoped IJSRuntime and NavigationManager instances",
            !ReferenceEquals(page1.JS, page2.JS) && !ReferenceEquals(page1.Navigation, page2.Navigation));

        // Step 3: the actor's captured services act on Blazor's dispatcher (through the actor's own render handle).
        ProbePage actor = mode == "control" ? page2 : page1;
        string tag = mode == "control" ? "page-2-control" : "page-1-late";
        using var jsTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        using var ownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Task? js = null;
        Task<string>? own = null;
        string navigation = "", render = "";
        await actor.OnDispatcher(() => {
            if (mode == "crosstalk") own = page2.JS.InvokeAsync<string>("probe.page2own", ownTimeout.Token).AsTask();
            js = actor.JS.InvokeVoidAsync("probe.stamp", jsTimeout.Token, $"{tag}-js").AsTask();
            navigation = Capture(() => actor.Navigation.NavigateTo($"/{tag}-navigation"));
            render = Capture(actor.Rerender);
        });
        report.Line("STEP", $"3 issued by page {actor.Number}: InvokeVoidAsync(\"probe.stamp\", 500 ms token, \"{tag}-js\"), " +
            $"NavigateTo(\"/{tag}-navigation\"), StateHasChanged()");
        await Task.Delay(1000);

        // Step 4: what the provider was asked to deliver after step 2, and to which document.
        SentMessage[] sends = [.. host.Sent];
        var timeline = sends.Select(s => (s.Seq, Print: (Action)(() => report.Send(s, s.Seq > mark))))
            .Concat(host.Inbound.Select(m => (m.Seq, Print: (Action)(() => report.Page(m.Seq, m.Document, m.Message)))));
        foreach ((int _, Action print) in timeline.OrderBy(static entry => entry.Seq).ToArray()) print();
        SentMessage[] late = [.. sends.Where(s => s.Seq > mark && (s.Message.Contains("probe.stamp", StringComparison.Ordinal)
            || s.Message.Contains($"{tag}-navigation", StringComparison.Ordinal) || s.Type == "RenderBatch"))];
        foreach (SentMessage sent in late)
            report.Line("OBSERVE", $"after step 2: send #{sent.Seq} {sent.Type} named document {sent.Document} " +
                $"({(sent.Delivered ? "delivered" : "refused: retired")})", sent.Short);
        if (late.Length == 0) report.Line("OBSERVE", "after step 2: no send contains probe.stamp, the navigation marker or RenderBatch");
        report.Line("OBSERVE", $"page {actor.Number} NavigateTo call", navigation);
        report.Line("OBSERVE", $"page {actor.Number} StateHasChanged call", render);
        report.Line("OBSERVE", $"page {actor.Number} InvokeVoidAsync task after ~1 s", Outcome(js));

        bool jsTo2 = late.Any(s => s is { Delivered: true, Document: 2, Type: "BeginInvokeJS" } && s.Message.Contains($"{tag}-js", StringComparison.Ordinal));
        bool navTo2 = late.Any(s => s is { Delivered: true, Document: 2, Type: "Navigate" });
        bool renderTo2 = late.Any(s => s is { Delivered: true, Document: 2, Type: "RenderBatch" });
        report.Gap = new { js = jsTo2, navigation = navTo2, render = renderTo2 };
        report.Line(mode == "control" ? "ROUTING" : "GAP", $"js={(jsTo2 ? "sent with document 2" : "not sent to document 2")}; " +
            $"navigation={(navTo2 ? "sent with document 2" : "not sent to document 2")}; " +
            $"render={(renderTo2 ? "sent with document 2" : "not sent to document 2")}");

        if (mode == "control") {
            report.Result = jsTo2 && navTo2 && renderTo2
                ? "control passed (page 2's own JS call, navigation and render were sent with document 2)"
                : "control FAILED (page 2's own output did not reach document 2: the harness cannot tell the cases apart)";
            if (!(jsTo2 && navTo2 && renderTo2)) report.Fail(new InvalidOperationException(report.Result));
        }
        else report.Result = jsTo2 || navTo2 || renderTo2 ? "gap reproduced" : "not reproduced";

        if (mode == "crosstalk") {
            long? page1Task = late.FirstOrDefault(s => s.Message.Contains("\"probe.stamp\"", StringComparison.Ordinal))?.Arg1;
            long? page2Task = sends.FirstOrDefault(s => s.Message.Contains("\"probe.page2own\"", StringComparison.Ordinal))?.Arg1;
            report.Line("OBSERVE", "task ids: page 1's probe.stamp call / page 2's own probe.page2own call", $"{page1Task} / {page2Task}");
            report.Line("OBSERVE", "page 2's own probe.page2own task (the page never answered it)",
                own is { IsCompletedSuccessfully: true } ? $"completed successfully with {(own.Result is null ? "null" : $"\"{own.Result}\"")}" : Outcome(own));
            bool crossTalk = own is { IsCompletedSuccessfully: true };
            report.Line("CROSSTALK", crossTalk
                ? "page 2's own pending JS call was completed by the answer the page sent for page 1's call"
                : "page 2's own pending JS call was not completed by page 1's answer");
            report.Gap = new { js = jsTo2, navigation = navTo2, render = renderTo2, crossTalk };
        }

        await window.Features.Lifecycle.CloseAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static string Capture(Action action) {
        try {
            action();
            return "returned without exception";
        }
        catch (Exception exception) {
            return $"threw {exception.GetType().Name}: {exception.Message}";
        }
    }

    private static string Outcome(Task? task) => task switch {
        null => "not started",
        { IsCompletedSuccessfully: true } => "completed successfully",
        { IsCanceled: true } => "cancelled by its timeout token (no answer reached this IJSRuntime)",
        { IsFaulted: true } => $"faulted: {task.Exception!.GetBaseException().GetType().Name}: {task.Exception.GetBaseException().Message}",
        _ => "still pending"
    };

    private static async Task Until(Func<bool> condition, string what, int timeoutMs = 10000) {
        var clock = Stopwatch.StartNew();
        while (!condition()) {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }
}

/// <summary>The root component. Every page context (one per AttachPage) creates one instance with its own scoped services.</summary>
public sealed class ProbePage : ComponentBase {
    private static readonly List<ProbePage> Pages = [];
    private string _text = "initial";

    [Inject] public IJSRuntime JS { get; set; } = default!;
    [Inject] public NavigationManager Navigation { get; set; } = default!;
    public int Number { get; private set; }

    public static int Count {
        get {
            lock (Pages) return Pages.Count;
        }
    }

    public static ProbePage Get(int number) {
        lock (Pages) return Pages[number - 1];
    }

    protected override void OnInitialized() {
        lock (Pages) {
            Pages.Add(this);
            Number = Pages.Count;
        }
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddMarkupContent(0, $"<p>page {Number}: {_text}</p>");

    /// <summary>Runs on Blazor's dispatcher through this component's own render handle (ComponentBase.InvokeAsync).</summary>
    public Task OnDispatcher(Action action) => InvokeAsync(action);

    public void Rerender() {
        _text = "late";
        StateHasChanged();
    }
}

/// <summary>Prints one line per observation and writes the JSON report.</summary>
internal sealed class Report(string mode) {
    private readonly List<object> _lines = [];
    private readonly List<object> _sends = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string? _failure;

    public MemoryLogs Logs { get; } = new();
    public string Result { get; set; } = "harness failed";
    public object? Gap { get; set; }

    public void Line(string kind, string text, object? value = null) {
        lock (_lines) _lines.Add(new { atMs = _clock.ElapsedMilliseconds, kind, text, value = value?.ToString() });
        Console.WriteLine($"{kind} [{mode}] {text}{(value is null ? "" : $" :: {value}")}");
    }

    public void Send(SentMessage sent, bool afterStep2) {
        _sends.Add(new { sent.Seq, sent.Document, Direction = "host -> page (SendMessageAsync)", sent.Delivered, sent.Type, afterStep2, payload = sent.Short });
        Console.WriteLine($"SEND [{mode}] #{sent.Seq} document={sent.Document} {(sent.Delivered ? "delivered" : "refused")} {sent.Type}" +
            $"{(afterStep2 ? " (after step 2)" : "")} :: {(sent.Type == "RenderBatch" ? $"batch {sent.Arg1}" : sent.Short)}");
    }

    public void Page(int seq, ulong document, string message) {
        _sends.Add(new { Seq = seq, Document = document, Direction = "page -> host (MessageReceived)", payload = message });
        Console.WriteLine($"PAGE [{mode}] #{seq} document={document} -> host :: {message}");
    }

    public void Fail(Exception exception) {
        _failure ??= exception.ToString();
        Line("HARNESS", $"failure: {exception.GetType().Name}", exception.Message);
    }

    private static string InfiniFrameVersion => typeof(Report).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(static attribute => attribute.Key == "InfiniFrameVersion")?.Value ?? "unknown";

    public int Finish(string path) {
        foreach (string entry in Logs.Entries.Where(static e => e.Contains("etired", StringComparison.Ordinal) || e.StartsWith("Warn", StringComparison.Ordinal)
                     || e.StartsWith("Err", StringComparison.Ordinal)))
            Console.WriteLine($"LOG [{mode}] {entry}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new {
            mode, result = Result, gap = Gap, harnessFailure = _failure, packages = $"InfiniLore.InfiniFrame.* {InfiniFrameVersion}",
            runtime = Environment.Version.ToString(), lines = _lines, sends = _sends, coreLog = Logs.Entries.ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"REPORT [{mode}] {path}");
        Console.WriteLine($"REPRO RESULT: {Result}");
        return _failure is null ? 0 : 2;
    }
}

/// <summary>Collects InfiniFrame and Blazor log entries (Debug and above) for the report.</summary>
internal sealed class MemoryLogs : ILoggerProvider {
    public ConcurrentQueue<string> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void Dispose() {}

    private sealed class Logger(MemoryLogs owner, string category) : ILogger {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug
            && (category.StartsWith("InfiniFrame", StringComparison.Ordinal) || category.StartsWith("Microsoft.AspNetCore.Components", StringComparison.Ordinal));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
            if (IsEnabled(logLevel))
                owner.Entries.Enqueue($"{logLevel} {category}: {formatter(state, exception)}{(exception is null ? "" : $" [{exception.GetType().Name}]")}");
        }
    }
}
