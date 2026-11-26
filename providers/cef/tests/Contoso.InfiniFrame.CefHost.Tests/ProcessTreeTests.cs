using System.Text.Json;
using Contoso.InfiniFrame.CefHost.Tests.Infrastructure;

namespace Contoso.InfiniFrame.CefHost.Tests;

/// <summary>
///     7. The job object holds the companion and every descendant. The fake starts a Python grandchild after hello
///     (as the companion starts CEF's processes only after hello) and records its id; the test identifies it by id and
///     creation time. Nothing is ever killed by the test except, after a failure, exactly a recorded process.
/// </summary>
/// <remarks>
///     Console-subsystem processes started without a window each get a <c>conhost.exe</c>, created by the process
///     itself while it starts. The grandchild's console host is therefore always in the job; the companion's own one
///     only when the companion reached console initialisation after the provider assigned it to the job. While both
///     run, the job holds 3 or 4 processes; 3 or more proves the grandchild (and its console host) are owned.
/// </remarks>
[Category("Fake")]
public sealed class ProcessTreeTests {
    private const string PidFile = "grandchild.json";
    private const int OwnedWithGrandchild = 3;
    private static readonly int TerminatedByJob = unchecked((int)0xC000013A);// CefProcessTree.Terminate's exit code

    [Test]
    [Timeout(60_000)]
    public async Task CompanionIgnoringEof_IsTerminatedWithItsGrandchild_AsExitEscalation(CancellationToken cancellationToken) {
        await using HostHarness harness = Create("tree-ignore", "tree-ignore-eof");
        await harness.StartAsync();
        ProcessIdentity grandchild = await GrandchildAsync(harness);
        int? activeWhileRunning = await ActiveProcessesAsync(harness);

        await harness.ShutdownAsync();
        TimeSpan disposal = await harness.DisposeHostAsync();
        bool grandchildAlive = grandchild.IsAlive();
        bool companionAlive = harness.Companion?.IsAlive() ?? true;
        CefHostDiagnostics final = harness.Diagnostics;
        // Informational (no expectation stated): how an escalation during disposal surfaces as a fault and on Completion.
        await Task.WhenAny(harness.Host.Completion, Task.Delay(500));
        Console.WriteLine($"[process-tree] after escalated disposal: Completion {harness.Host.Completion.Status}; session faults [{string.Join(" | ", final.SessionFaults)}]");

        using (Assert.Multiple()) {
            await Assert.That(final.ProcessTracking).IsEqualTo("job object");
            await Assert.That(activeWhileRunning ?? 0).IsGreaterThanOrEqualTo(OwnedWithGrandchild).Because("the job holds the companion, its grandchild and the grandchild's console host");
            await Assert.That(final.ExitEscalated).IsTrue().Because("the companion ignored EOF beyond the exit timeout");
            await Assert.That(final.DescendantsEscalated).IsFalse().Because("terminating the job ended the grandchild too");
            await Assert.That(final.ExitUnconfirmed).IsFalse();
            await Assert.That(final.CompanionExitCode).IsEqualTo(TerminatedByJob);
            await Assert.That(grandchildAlive).IsFalse().Because($"grandchild {grandchild} must be gone after disposal");
            await Assert.That(companionAlive).IsFalse();
            await Assert.That(final.OwnedProcessesRemaining).IsEqualTo(0);
            await Assert.That(final.OwnedProcessesStarted ?? 0).IsGreaterThanOrEqualTo(2);
            await Assert.That(disposal).IsLessThan(harness.Options.ExitTimeout + TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task GrandchildOutlivingTheCompanion_IsTerminated_AsDescendantEscalation(CancellationToken cancellationToken) {
        await using HostHarness harness = Create("tree-orphan", "tree-orphan-on-eof");
        await harness.StartAsync();
        ProcessIdentity grandchild = await GrandchildAsync(harness);
        int? activeWhileRunning = await ActiveProcessesAsync(harness);

        await harness.ShutdownAsync();
        TimeSpan disposal = await harness.DisposeHostAsync();
        bool grandchildAlive = grandchild.IsAlive();
        CefHostDiagnostics final = harness.Diagnostics;

        using (Assert.Multiple()) {
            await Assert.That(final.ProcessTracking).IsEqualTo("job object");
            await Assert.That(activeWhileRunning ?? 0).IsGreaterThanOrEqualTo(OwnedWithGrandchild).Because("the job holds the companion, its grandchild and the grandchild's console host");
            await Assert.That(final.CompanionExitCode).IsEqualTo(0).Because("the companion itself exited normally on EOF");
            await Assert.That(final.ExitEscalated).IsFalse();
            await Assert.That(final.DescendantsEscalated).IsTrue().Because("the grandchild outlived the companion beyond the exit timeout");
            await Assert.That(final.OwnedProcessesAliveAtCompanionExit ?? 0).IsGreaterThanOrEqualTo(1);
            await Assert.That(grandchildAlive).IsFalse().Because($"grandchild {grandchild} must be gone after disposal");
            await Assert.That(final.OwnedProcessesRemaining).IsEqualTo(0);
            await Assert.That(disposal).IsLessThan(harness.Options.ExitTimeout * 2 + TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task TreeEndingWithTheCompanion_IsNotEscalated(CancellationToken cancellationToken) {
        await using HostHarness harness = Create("tree-normal", "tree-normal");
        await harness.StartAsync();
        ProcessIdentity grandchild = await GrandchildAsync(harness);
        int? activeWhileRunning = await ActiveProcessesAsync(harness);

        await harness.ShutdownAsync();
        TimeSpan disposal = await harness.DisposeHostAsync();
        bool grandchildAlive = grandchild.IsAlive();
        CefHostDiagnostics final = harness.Diagnostics;
        IReadOnlyList<ProcessIdentity> leftovers = await harness.LeftoverProcessesAsync();

        using (Assert.Multiple()) {
            await Assert.That(final.ProcessTracking).IsEqualTo("job object");
            await Assert.That(activeWhileRunning ?? 0).IsGreaterThanOrEqualTo(OwnedWithGrandchild).Because("the job holds the companion, its grandchild and the grandchild's console host");
            await Assert.That(final.CompanionExitCode).IsEqualTo(0);
            await Assert.That(final.ExitEscalated).IsFalse();
            await Assert.That(final.DescendantsEscalated).IsFalse();
            await Assert.That(grandchildAlive).IsFalse();
            await Assert.That(final.OwnedProcessesRemaining).IsEqualTo(0);
            await Assert.That(final.SessionFaults.Count).IsEqualTo(0);
            await Assert.That(harness.Host.Completion.IsCompletedSuccessfully).IsTrue().Because("an orderly stop completes the host");
            await Assert.That(disposal).IsLessThan(harness.Options.ExitTimeout + TimeSpan.FromSeconds(2));
            await Assert.That(leftovers.Count).IsEqualTo(0);
        }
    }

    private static HostHarness Create(string tag, string mode) =>
        HostHarness.Create(tag, mode, static directory => ["--pid-file", directory.PathOf(PidFile)],
            static options => options.ExitTimeout = TimeSpan.FromSeconds(1));

    /// <summary>Reads the grandchild's id from the fake and pins its identity (id plus creation time) while it runs.</summary>
    private static async Task<ProcessIdentity> GrandchildAsync(HostHarness harness) {
        string path = harness.Directory.PathOf(PidFile);
        await Wait.UntilAsync(() => File.Exists(path), "the fake's grandchild PID file");
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        int pid = document.RootElement.GetProperty("pid").GetInt32();
        ProcessIdentity grandchild = ProcessIdentity.TryCapture(pid)
            ?? throw new InvalidOperationException($"The fake's grandchild {pid} is not running.");
        harness.Track(grandchild);
        return grandchild;
    }

    /// <summary>The job's live process count once the grandchild and its console host are in it (or the last value seen).</summary>
    private static async Task<int?> ActiveProcessesAsync(HostHarness harness) {
        int? active = null;
        try {
            await Wait.UntilAsync(() => (active = harness.Cef.Connection.Tree.ActiveProcesses) >= OwnedWithGrandchild,
                $"{OwnedWithGrandchild} or more live processes in the job", TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException) {
            // Reported by the assertion on the returned value.
        }
        Console.WriteLine($"[process-tree] live processes in the job while the grandchild runs: {active?.ToString() ?? "unknown"}");
        return active;
    }
}
