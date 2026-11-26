using System.ComponentModel;
using System.Diagnostics;

// Every test drives one companion (fake or real) and asserts on timing bounds (cancellation after 200 ms, a 1 s exit
// timeout, a 1 s resource timeout), so the suite runs strictly sequentially for determinism.
[assembly: NotInParallel]

namespace Contoso.InfiniFrame.CefHost.Tests.Infrastructure;

/// <summary>
///     Locations used by the suite, from environment variables: the interpreter for the fake companion
///     (CEFHOST_TESTS_PYTHON), the only temporary root (CEFHOST_TESTS_TEMP) and the optional real payload
///     (CEFHOST_TESTS_PAYLOAD).
/// </summary>
internal static class TestEnvironment {
    /// <summary>The Python 3 used for the fake companion (standard library only, always run with <c>-I</c>).</summary>
    internal static string Python { get; } = Environment.GetEnvironmentVariable("CEFHOST_TESTS_PYTHON") is { Length: > 0 } python
        ? python
        : OperatingSystem.IsWindows() ? "python" : "python3";

    internal static string FakeCompanionScript { get; } = Path.Combine(AppContext.BaseDirectory, "fake_companion.py");

    /// <summary>
    ///     Every temporary path of this suite lives below this directory. Keep it short on Windows: the real companion
    ///     nests Chromium's profile several levels below it.
    /// </summary>
    internal static string TempRoot { get; } = Environment.GetEnvironmentVariable("CEFHOST_TESTS_TEMP") is { Length: > 0 } temp
        ? Path.GetFullPath(temp)
        : Path.Combine(Path.GetTempPath(), "cefhost-tests");

    /// <summary>A verified real win-x64 companion payload directory for the optional real-companion test, or empty.</summary>
    internal static string RealPayload { get; } = Environment.GetEnvironmentVariable("CEFHOST_TESTS_PAYLOAD") is { Length: > 0 } payload
        ? Path.GetFullPath(payload)
        : "";
}

/// <summary>A per-test directory below <see cref="TestEnvironment.TempRoot" />, removed when the test ends.</summary>
internal sealed class TestDirectory : IDisposable {
    internal TestDirectory(string tag) {
        // Short names: the real companion nests Chromium's profile several levels below the state root.
        Root = Path.Combine(TestEnvironment.TempRoot, $"{tag}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(Root);
    }

    internal string Root { get; }
    internal string StateRoot => Path.Combine(Root, "s");
    internal string PathOf(string name) => Path.Combine(Root, name);

    /// <summary>Set when the directory could not be removed (reported, never hidden).</summary>
    internal string? RemovalFailure { get; private set; }

    public void Dispose() {
        for (int attempt = 1; attempt <= 20; attempt++) {
            try {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                RemovalFailure = null;
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                RemovalFailure = exception.Message;
                Thread.Sleep(100);
            }
        }
        Console.WriteLine($"[test-directory] could not remove {Root}: {RemovalFailure}");
    }
}

/// <summary>
///     A process identified by id <em>and</em> creation time, so a recycled id is never mistaken for it. Liveness checks
///     and the emergency cleanup act only on that exact process, never on anything matched by name.
/// </summary>
internal sealed record ProcessIdentity(int Id, DateTime StartTimeUtc) {
    internal static ProcessIdentity? TryCapture(int id) {
        try {
            using var process = Process.GetProcessById(id);
            return process.HasExited ? null : new ProcessIdentity(id, process.StartTime.ToUniversalTime());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) {
            return null;
        }
    }

    internal bool IsAlive() {
        try {
            using var process = Process.GetProcessById(Id);
            return !process.HasExited && process.StartTime.ToUniversalTime() == StartTimeUtc;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) {
            // Gone, or the id now belongs to a process this user cannot even open (so not one of ours).
            return false;
        }
    }

    /// <summary>Emergency cleanup after a failed test: terminates exactly this process if it is still the same one.</summary>
    internal bool KillIfAlive() {
        try {
            using var process = Process.GetProcessById(Id);
            if (process.HasExited || process.StartTime.ToUniversalTime() != StartTimeUtc) return false;
            process.Kill();
            process.WaitForExit(5000);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) {
            return false;
        }
    }

    public override string ToString() => $"pid {Id} started {StartTimeUtc:O}";
}
