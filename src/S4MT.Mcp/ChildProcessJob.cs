using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace S4MT.Mcp;

internal sealed class ChildProcessJob : IDisposable
{
    private static ChildProcessJob? hostLifetime;
    private readonly SafeFileHandle? handle;

    internal static void ContainHostLifetime()
    {
        hostLifetime = new ChildProcessJob();
        using var host = Process.GetCurrentProcess();
        hostLifetime.Attach(host);
    }

    internal ChildProcessJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        handle = CreateJobObjectW(IntPtr.Zero, null);
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (handle.IsInvalid || !SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
            throw new McpFailure("cli_unavailable", $"Could not create child process job: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    internal void Attach(Process child)
    {
        if (handle is not null && !AssignProcessToJobObject(handle, child.Handle))
            throw new McpFailure("cli_unavailable", $"Could not contain CLI process: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    internal async Task StopAsync(Process child)
    {
        if (handle is null)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            return;
        }
        if (!TerminateJobObject(handle, 130))
            throw new McpFailure("cleanup_failed", "Could not terminate the CLI process job.");
        using var deadline = new CancellationTokenSource(5000);
        while (true)
        {
            if (!QueryInformationJobObject(handle, 1, out var accounting, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero))
                throw new McpFailure("cleanup_failed", "Could not inspect the CLI process job.");
            if (accounting.ActiveProcesses == 0) return;
            try { await Task.Delay(10, deadline.Token); }
            catch (OperationCanceledException) { throw new McpFailure("cleanup_failed", "CLI process job did not stop within five seconds."); }
        }
    }

    public void Dispose() => handle?.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int informationClass, out Accounting information, uint length, IntPtr returnedLength);
}
