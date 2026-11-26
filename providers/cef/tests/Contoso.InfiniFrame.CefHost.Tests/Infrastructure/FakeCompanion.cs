using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Contoso.InfiniFrame.CefHost.Tests.Infrastructure;

/// <summary>
///     How a test launches <c>fake_companion.py</c> (through the provider's test seam) and what the fake logged: every
///     frame it received (<c>command</c>), wrote (<c>sent</c>) and decided (<c>action</c>, <c>lifecycle</c>).
/// </summary>
internal sealed class FakeCompanion {
    internal FakeCompanion(TestDirectory directory, string mode, IReadOnlyList<string> arguments) {
        Mode = mode;
        Arguments = arguments;
        LogPath = directory.PathOf("fake-log.jsonl");
    }

    internal string Mode { get; }
    internal IReadOnlyList<string> Arguments { get; }
    internal string LogPath { get; }

    /// <summary>The provider's <c>testLaunch</c>: the fake runs in the provider-created run directory.</summary>
    internal CefLaunch Launch(string runDirectory) =>
        new(TestEnvironment.Python,
            ["-I", TestEnvironment.FakeCompanionScript, "--mode", Mode, "--log", LogPath, .. Arguments],
            runDirectory,
            new Dictionary<string, string>());

    internal IReadOnlyList<FakeLogEntry> ReadLog() {
        if (!File.Exists(LogPath)) return [];
        string text;
        using (var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            text = reader.ReadToEnd();
        var entries = new List<FakeLogEntry>();
        foreach (string line in text.Split('\n')) {
            if (line.Length == 0) continue;
            try {
                using JsonDocument document = JsonDocument.Parse(line);
                entries.Add(new FakeLogEntry(document.RootElement.Clone()));
            }
            catch (JsonException) {
                // The last line may still be being written.
            }
        }
        return entries;
    }

    /// <summary>Frames of <paramref name="kind" /> the fake received from the provider.</summary>
    internal IReadOnlyList<FakeLogEntry> Received(string kind) => [.. ReadLog().Where(entry => entry.IsCommand(kind))];

    internal async Task<FakeLogEntry> WaitForAsync(Func<FakeLogEntry, bool> predicate, string description, TimeSpan? timeout = null) {
        FakeLogEntry? found = null;
        await Wait.UntilAsync(() => (found = ReadLog().FirstOrDefault(predicate)) is not null, $"the fake to log {description}",
            timeout ?? Wait.Step).ConfigureAwait(false);
        return found!;
    }

    /// <summary>A compact rendering of the log for test output (bodies are never logged by the fake).</summary>
    internal string Render(int maxLines = 80) {
        IReadOnlyList<FakeLogEntry> entries = ReadLog();
        IEnumerable<FakeLogEntry> shown = entries.Count > maxLines ? entries.Skip(entries.Count - maxLines) : entries;
        var builder = new StringBuilder();
        if (entries.Count > maxLines) builder.AppendLine($"  ... {entries.Count - maxLines} earlier entries omitted");
        foreach (FakeLogEntry entry in shown) {
            string json = entry.Json.GetRawText();
            builder.Append("  ").AppendLine(json.Length > 400 ? json[..400] + "…" : json);
        }
        return builder.ToString();
    }
}

/// <summary>One line of the fake's JSONL log.</summary>
internal sealed record FakeLogEntry(JsonElement Json) {
    internal long Seq => Json.GetProperty("seq").GetInt64();
    internal string Type => Json.GetProperty("type").GetString() ?? "";
    internal string? Kind => Str("kind");
    internal ulong Request => U64("request");
    internal ulong View => U64("view");
    internal ulong Generation => U64("generation");
    internal bool IsCommand(string kind) => Type == "command" && Kind == kind;
    internal bool IsSent(string kind) => Type == "sent" && Kind == kind;
    internal bool IsAction(string action) => Type == "action" && Str("action") == action;
    internal bool IsLifecycle(string lifecycleEvent) => Type == "lifecycle" && Str("event") == lifecycleEvent;

    internal string? Str(string name) => Json.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal long? Int(string name) => Json.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
    private ulong U64(string name) => Json.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetUInt64() : 0;
}

/// <summary>Bounded waiting: every wait in this suite ends with a descriptive failure instead of blocking.</summary>
internal static class Wait {
    /// <summary>The default bound for one test step (a command round trip, an event, a disposal).</summary>
    internal static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    internal static async Task UntilAsync(Func<bool> condition, string description, TimeSpan? timeout = null) {
        TimeSpan limit = timeout ?? Step;
        var clock = Stopwatch.StartNew();
        while (!condition()) {
            if (clock.Elapsed > limit) throw new TimeoutException($"Timed out after {limit.TotalSeconds:0.#} s waiting for {description}.");
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    internal static async Task<T> WithinAsync<T>(Task<T> task, string description, TimeSpan? timeout = null) {
        TimeSpan limit = timeout ?? Step;
        try {
            return await task.WaitAsync(limit).ConfigureAwait(false);
        }
        catch (TimeoutException exception) when (!task.IsCompleted) {
            throw new TimeoutException($"Timed out after {limit.TotalSeconds:0.#} s waiting for {description}.", exception);
        }
    }

    internal static async Task WithinAsync(Task task, string description, TimeSpan? timeout = null) {
        TimeSpan limit = timeout ?? Step;
        try {
            await task.WaitAsync(limit).ConfigureAwait(false);
        }
        catch (TimeoutException exception) when (!task.IsCompleted) {
            throw new TimeoutException($"Timed out after {limit.TotalSeconds:0.#} s waiting for {description}.", exception);
        }
    }

    /// <summary>Runs <paramref name="action" /> and returns what it threw (null when it completed), bounded.</summary>
    internal static async Task<Exception?> ExceptionAsync(Func<Task> action, string description, TimeSpan? timeout = null) {
        Task task;
        try {
            task = action();
        }
        catch (Exception exception) {
            return exception;
        }
        try {
            await WithinAsync(task, description, timeout).ConfigureAwait(false);
            return null;
        }
        catch (TimeoutException) when (!task.IsCompleted) {
            throw;
        }
        catch (Exception exception) {
            return exception;
        }
    }
}
