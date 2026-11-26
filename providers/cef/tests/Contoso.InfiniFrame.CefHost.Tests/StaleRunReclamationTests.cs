using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Contoso.InfiniFrame.CefHost.Tests.Infrastructure;

namespace Contoso.InfiniFrame.CefHost.Tests;

/// <summary>
///     11. At start the provider removes run directories (<c>run-&lt;yyyyMMddTHHmmssfff&gt;-&lt;pid&gt;-&lt;8 hex&gt;</c>) left by
///     applications that are gone, and never touches runs of live applications (this process included) or entries
///     not named exactly as it names runs.
/// </summary>
[Category("Fake")]
public sealed class StaleRunReclamationTests {
    [Test]
    [Timeout(60_000)]
    public async Task StartAsync_ReclaimsOnlyTheRunsOfDeadProcesses(CancellationToken cancellationToken) {
        await using HostHarness harness = HostHarness.Create("stale-runs", "normal");
        System.IO.Directory.CreateDirectory(harness.Options.StateRoot);
        // The live owner starts first, so the short-lived one below cannot be handed a recycled id of it.
        using Process sleeper = StartPython("import sys; sys.stdin.buffer.read()", redirectInput: true);
        var sleeperIdentity = new ProcessIdentity(sleeper.Id, sleeper.StartTime.ToUniversalTime());
        harness.Track(sleeperIdentity);
        try {
            (int deadPid, DateTime deadStarted) = RunShortLivedProcess();
            string dead = RunName(deadStarted, deadPid);// timestamped before its owner exited
            string own = RunName(DateTime.UtcNow, Environment.ProcessId);
            string live = RunName(DateTime.UtcNow, sleeper.Id);// timestamped after its owner started
            const string foreign = "run-foo";
            foreach (string name in new[] { dead, own, live, foreign }) System.IO.Directory.CreateDirectory(Path.Combine(harness.Options.StateRoot, name, "state"));
            bool deadIdInUse = ProcessIdentity.TryCapture(deadPid) is not null;

            await harness.StartAsync();
            CefHostDiagnostics started = harness.Diagnostics;
            bool Exists(string name) => System.IO.Directory.Exists(Path.Combine(harness.Options.StateRoot, name));
            (bool Dead, bool Own, bool Live, bool Foreign) present = (Exists(dead), Exists(own), Exists(live), Exists(foreign));

            await harness.ShutdownAsync();
            await harness.DisposeHostAsync();
            CefHostDiagnostics final = harness.Diagnostics;
            string[] remaining = [.. System.IO.Directory.EnumerateDirectories(harness.Options.StateRoot).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

            using (Assert.Multiple()) {
                await Assert.That(deadIdInUse).IsFalse().Because($"precondition: the dead owner's id {deadPid} was not recycled before the check");
                await Assert.That(string.Join(",", started.StaleRunsReclaimed)).IsEqualTo(dead);
                await Assert.That(present.Dead).IsFalse().Because("the dead process's run is removed");
                await Assert.That(present.Own).IsTrue().Because("runs of this process are never touched");
                await Assert.That(present.Live).IsTrue().Because("runs of a live process are never touched");
                await Assert.That(present.Foreign).IsTrue().Because("entries not named exactly as runs are never touched");
                await Assert.That(string.Join(",", remaining)).IsEqualTo(string.Join(",", new[] { foreign, live, own }.Order(StringComparer.Ordinal)))
                    .Because("after disposal only the provider's own new run is gone in addition");
                await Assert.That(final.Cleanup).StartsWith("clean");
            }
        }
        finally {
            // The live owner ends when its input closes; only if it did not is that exact process terminated.
            sleeper.StandardInput.Close();
            if (!sleeper.WaitForExit(5000)) sleeperIdentity.KillIfAlive();
        }
    }

    private static string RunName(DateTime createdUtc, int pid) =>
        $"run-{createdUtc.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture)}-{pid}-{RandomNumberGenerator.GetHexString(8, lowercase: true)}";

    /// <summary>Starts and waits for a short-lived process; returns its id and start time once it has exited.</summary>
    private static (int Pid, DateTime StartedUtc) RunShortLivedProcess() {
        using Process process = StartPython("pass", redirectInput: false);
        DateTime started = process.StartTime.ToUniversalTime();
        if (!process.WaitForExit(15_000)) throw new TimeoutException("The short-lived process did not exit.");
        return (process.Id, started);
    }

    private static Process StartPython(string code, bool redirectInput) {
        var start = new ProcessStartInfo(TestEnvironment.Python) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = redirectInput };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(code);
        return Process.Start(start) ?? throw new InvalidOperationException("Python did not start.");
    }
}
