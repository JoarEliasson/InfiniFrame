using System.Text;
using System.Text.RegularExpressions;

namespace Contoso.InfiniFrame.CefHost;

/// <summary>
///     Keeps a bounded, redacted tail of the companion's stderr: at most <see cref="MaxLines" /> lines of at most
///     <see cref="MaxLineChars" /> characters each. Older lines are dropped and counted; long lines are truncated and
///     counted. Nothing else is retained, so a child emitting unbounded output cannot grow managed memory.
/// </summary>
internal sealed partial class CefStderrTail {
    internal const int MaxLines = 64;
    internal const int MaxLineChars = 512;
    private readonly object _gate = new();
    private readonly Queue<string> _lines = new();
    private readonly StringBuilder _current = new();
    private bool _currentTruncated;
    private long _droppedLines;
    private long _truncatedLines;
    private long _totalChars;

    internal void Append(ReadOnlySpan<char> chunk) {
        lock (_gate) {
            _totalChars += chunk.Length;
            foreach (char character in chunk) {
                if (character == '\n') {
                    CompleteLine();
                    continue;
                }
                if (character == '\r') continue;
                if (_current.Length < MaxLineChars) _current.Append(char.IsControl(character) ? '?' : character);
                else _currentTruncated = true;
            }
        }
    }

    internal void Flush() {
        lock (_gate) {
            if (_current.Length > 0 || _currentTruncated) CompleteLine();
        }
    }

    internal (IReadOnlyList<string> Lines, long DroppedLines, long TruncatedLines, long TotalChars) Snapshot() {
        lock (_gate) return ([.. _lines], _droppedLines, _truncatedLines, _totalChars);
    }

    /// <summary>
    ///     The most recent lines, newest first (a truncated message keeps the causal last line), joined for an exception
    ///     message. Already redacted and bounded.
    /// </summary>
    internal string Summary(int lines) {
        lock (_gate) {
            string[] tail = [.. _lines.Skip(Math.Max(0, _lines.Count - lines)).Reverse()];
            return tail.Length == 0 ? "(no companion stderr retained)" : "(newest first) " + string.Join(" | ", tail);
        }
    }

    private void CompleteLine() {
        string line = Redact(_current.ToString());
        if (_currentTruncated) {
            line += " [truncated]";
            _truncatedLines++;
        }
        _current.Clear();
        _currentTruncated = false;
        _lines.Enqueue(line);
        while (_lines.Count > MaxLines) {
            _lines.Dequeue();
            _droppedLines++;
        }
    }

    /// <summary>Removes secrets and personal paths from one diagnostic line.</summary>
    internal static string Redact(string line) {
        // Header credentials first: everything after the header name is a credential (Bearer/Basic/Negotiate values,
        // multi-pair cookies). A shorter key=value rule running first would consume only the scheme word.
        line = HeaderCredential().Replace(line, static match => $"{match.Groups[1].Value}{match.Groups[2].Value}[redacted]");
        line = Bearer().Replace(line, "Bearer [redacted]");
        line = SecretAssignment().Replace(line, static match => $"{match.Groups[1].Value}{match.Groups[2].Value}[redacted]");
        line = WindowsUserPath().Replace(line, @"%USERPROFILE%\");
        line = PosixHomePath().Replace(line, "~/");
        line = LongToken().Replace(line, "[redacted-token]");
        return line;
    }

    [GeneratedRegex(@"(?i)\b(proxy-authorization|authorization|set-cookie|cookie)(\s*[=:]\s*)\S.*")]
    private static partial Regex HeaderCredential();

    [GeneratedRegex(@"(?i)\b(token|secret|password|passwd|pwd|api[_-]?key|authorization|cookie|nonce)(\s*[=:]\s*)(\S+)")]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/-]+=*")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?i)\b[A-Z]:\\Users\\[^\\\s]+\\")]
    private static partial Regex WindowsUserPath();

    [GeneratedRegex(@"/home/[^/\s]+/")]
    private static partial Regex PosixHomePath();

    [GeneratedRegex(@"\b[A-Za-z0-9+/_-]{48,}={0,2}")]
    private static partial Regex LongToken();
}
