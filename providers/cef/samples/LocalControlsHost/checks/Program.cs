using System.Security.Cryptography;
using System.Text.Json;
using LocalControlsHost;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

int checks = 0;
void Check(bool condition, string name) {
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine($"PASS: {name}");
    checks++;
}

// This mode opens only the expected-digest file and each supplied physical web root. It never resolves SDK
// manifests or reads the source tree/NuGet cache, so missing deployed framework assets cannot be masked.
if (args.Length >= 3 && args[0] == "--web-roots") {
    var required = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(args[1]))
        ?? throw new ArgumentException("Expected a JSON object mapping web paths to SHA-256 digests.");
    foreach (string root in args.Skip(2)) {
        using var files = new PhysicalFileProvider(Path.GetFullPath(root));
        foreach (var (path, expectedHash) in required) {
            var file = files.GetFileInfo('/' + path);
            Check(file.Exists, $"Required deployed asset exists: {path}");
            await using var stream = file.CreateReadStream();
            string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            Check(string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase), $"Deployed bytes match: {path}");
        }
        Check(!files.GetFileInfo("../Program.cs").Exists, "Physical web root does not expose parent source files");
    }
    Console.WriteLine($"Completed {checks} physical web-root checks without source/cache manifest lookup. No host or browser was started.");
    return;
}

if (args.Length != 3) throw new ArgumentException("Expected sample directory, copied build web root and copied publish web root; or --web-roots <digests.json> <web-root> [...].");

var defaults = HostArguments.Parse([]);
Check(defaults == new HostArguments(null, null, false), "App-local payload and temporary state defaults require no private path");
var paths = HostArguments.Parse(["--state-root", "state folder", "--payload", "verified runtime"]);
Check(paths.StateRoot == Path.GetFullPath("state folder") && paths.PayloadDirectory == Path.GetFullPath("verified runtime"),
    "CLI resolves explicit paths, including spaces");
Check(HostArguments.Parse(["--help"]).Help, "Help is parsed without runtime construction");
foreach (string[] invalid in new[] {
    new[] { "--payload" }, new[] { "--state-root" }, new[] { "--payload", "--help" },
    new[] { "--payload", " " }, new[] { "--unknown" },
    new[] { "--payload", "one", "--payload", "two" }, new[] { "--state-root", "one", "--state-root", "two" }
}) {
    bool rejected = false;
    try { HostArguments.Parse(invalid); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "Reject malformed CLI: " + string.Join(' ', invalid));
}

var logger = new CaptureLogger();
var exception = new InvalidOperationException("component binding failure");
await new HostErrorBoundaryLogger(logger).LogErrorAsync(exception);
Check(ReferenceEquals(logger.Exception, exception) && logger.Level == LogLevel.Error && logger.Message.Contains("LocalControlsUi.App", StringComparison.Ordinal),
    "ErrorBoundary logs the original exception at Error level with component context");

string sample = Path.GetFullPath(args[0]);
var expected = new Dictionary<string, string> {
    ["index.html"] = Path.Combine(sample, "wwwroot", "index.html"),
    ["host.css"] = Path.Combine(sample, "wwwroot", "host.css"),
    ["host-errors.js"] = Path.Combine(sample, "wwwroot", "host-errors.js"),
    ["_content/LocalControlsUi/local-controls.css"] = Path.Combine(sample, "..", "LocalControlsUi", "wwwroot", "local-controls.css"),
    ["_content/LocalControlsUi/local-controls.js"] = Path.Combine(sample, "..", "LocalControlsUi", "wwwroot", "local-controls.js"),
    ["_content/LocalControlsUi/module.svg"] = Path.Combine(sample, "..", "LocalControlsUi", "wwwroot", "module.svg")
};
foreach (string output in args.Skip(1)) {
    using var provider = new PhysicalFileProvider(Path.GetFullPath(output));
    foreach (var (path, source) in expected) {
        var file = provider.GetFileInfo('/' + path);
        Check(file.Exists, $"{Path.GetFileName(Path.GetDirectoryName(output))}: physical asset available at /{path}");
        await using var stream = file.CreateReadStream();
        byte[] actualHash = await SHA256.HashDataAsync(stream);
        byte[] sourceBytes = await File.ReadAllBytesAsync(source);
        Check(actualHash.SequenceEqual(SHA256.HashData(sourceBytes)), $"Copied bytes match {path}");
    }
    Check(!provider.GetFileInfo("../Program.cs").Exists, "Physical web root does not expose parent source files");
}
Console.WriteLine($"Completed {checks} offline helper/asset checks. No host, renderer, browser or CEF process was started.");

internal sealed class CaptureLogger : ILogger<HostErrorBoundaryLogger> {
    internal Exception? Exception { get; private set; }
    internal LogLevel Level { get; private set; }
    internal string Message { get; private set; } = "";
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        Exception = exception;
        Level = logLevel;
        Message = formatter(state, exception);
    }
}
