using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Contoso.InfiniFrame.CefHost;

/// <summary>
///     The companion and every process it starts (CEF's renderer, GPU and utility processes), tracked exactly.
/// </summary>
/// <remarks>
///     <para>
///         Windows: the companion joins a private, unnamed job object right after launch and before the hello
///         handshake. It starts no process before hello, so every descendant belongs to the job. That includes
///         descendants left behind by a companion crash, which a parent-id walk cannot find once the parent is gone.
///         <c>KILL_ON_JOB_CLOSE</c> ends the whole tree if this application process dies. Escalation terminates the
///         job: exactly the owned tree, never anything matched by name. Breakaway is permitted only on request
///         (Chromium does not request it on Windows 8 or later). Any process that broke away would show up as a
///         difference between the job's accounting and an external process census.
///     </para>
///     <para>
///         Other platforms, or a failed assignment: there is no job, and owned-tree termination falls back to
///         <see cref="Process.Kill(bool)" />, which walks parent links. The fallback and its reason are reported.
///     </para>
/// </remarks>
internal sealed class CefProcessTree : IDisposable {
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitBreakawayOk = 0x00000800;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly Process _process;
    private readonly SafeFileHandle? _job;

    private CefProcessTree(Process process, SafeFileHandle? job, string tracking) {
        _process = process;
        _job = job;
        Tracking = tracking;
    }

    /// <summary>"job object" or "parent-process walk (reason)".</summary>
    internal string Tracking { get; }
    internal bool IsJob => _job is not null;

    /// <summary>Assigns the just-started companion to a new job (Windows), or records the fallback.</summary>
    internal static CefProcessTree Attach(Process process, ILogger logger) {
        if (!OperatingSystem.IsWindows()) return new CefProcessTree(process, null, "parent-process walk (no job objects on this platform)");
        SafeFileHandle job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) return Fallback(process, job, logger, "CreateJobObject");
        var limits = new ExtendedLimitInformation { BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitBreakawayOk } };
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
            return Fallback(process, job, logger, "SetInformationJobObject");
        if (!AssignProcessToJobObject(job, process.SafeHandle)) return Fallback(process, job, logger, "AssignProcessToJobObject");
        return new CefProcessTree(process, job, "job object");
    }

    private static CefProcessTree Fallback(Process process, SafeFileHandle job, ILogger logger, string step) {
        int error = Marshal.GetLastPInvokeError();
        job.Dispose();
        string reason = $"parent-process walk ({step} failed: {new Win32Exception(error).Message})";
        logger.LogWarning("The CEF companion's process tree is not tracked by a job object: {Reason}", reason);
        return new CefProcessTree(process, null, reason);
    }

    /// <summary>Live processes in the tree, the companion included while it runs; null without a job.</summary>
    internal int? ActiveProcesses => Accounting() is {} accounting ? (int)accounting.ActiveProcesses : null;

    /// <summary>Every process that ever belonged to the tree, the companion included; null without a job.</summary>
    internal int? TotalProcesses => Accounting() is {} accounting ? (int)accounting.TotalProcesses : null;

    /// <summary>Peak committed memory of the whole tree and of its largest process (bytes); nulls without a job.</summary>
    internal (long? Tree, long? LargestProcess) PeakCommit {
        get {
            if (_job is null || _job.IsClosed) return (null, null);
            var limits = new ExtendedLimitInformation();
            return QueryInformationJobObject(_job, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>(), out _)
                ? ((long)limits.PeakJobMemoryUsed, (long)limits.PeakProcessMemoryUsed)
                : (null, null);
        }
    }

    /// <summary>The ids of the processes currently in the job (null without a job or if the query fails).</summary>
    internal int[]? ProcessIds() {
        if (_job is null || _job.IsClosed) return null;
        for (int capacity = 64; capacity <= 4096; capacity *= 4) {
            int bytes = 8 + capacity * IntPtr.Size;
            IntPtr buffer = Marshal.AllocHGlobal(bytes);
            try {
                if (QueryInformationJobObject(_job, JobObjectBasicProcessIdList, buffer, (uint)bytes, out _)) {
                    int listed = Marshal.ReadInt32(buffer, 4);// JOBOBJECT_BASIC_PROCESS_ID_LIST.NumberOfProcessIdsInList
                    var ids = new int[listed];
                    for (int i = 0; i < listed; i++) ids[i] = (int)Marshal.ReadIntPtr(buffer, 8 + i * IntPtr.Size);
                    return ids;
                }
                if (Marshal.GetLastPInvokeError() != 234) return null;// ERROR_MORE_DATA: retry larger
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }

    /// <summary>Waits until no owned process is alive. Without a job, only the companion itself can be awaited.</summary>
    internal async Task<bool> WaitForEmptyAsync(TimeSpan timeout) {
        var clock = Stopwatch.StartNew();
        while (true) {
            int? active = ActiveProcesses;
            if (active is null) return _process.HasExited;
            if (active == 0) return true;
            if (clock.Elapsed >= timeout) return false;
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    /// <summary>Escalation: terminates exactly the owned tree.</summary>
    internal void Terminate() {
        if (_job is not null && !_job.IsClosed) {
            if (!TerminateJobObject(_job, 0xC000013A)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return;
        }
        _process.Kill(entireProcessTree: true);
    }

    public void Dispose() => _job?.Dispose();

    private BasicAccountingInformation? Accounting() {
        if (_job is null || _job.IsClosed) return null;
        var accounting = new BasicAccountingInformation();
        return QueryInformationJobObject(_job, JobObjectBasicAccountingInformation, ref accounting,
            (uint)Marshal.SizeOf<BasicAccountingInformation>(), out _)
            ? accounting
            : null;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, ref BasicAccountingInformation information,
        uint length, out uint returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimitInformation information,
        uint length, out uint returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, IntPtr information, uint length, out uint returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccountingInformation {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
}
