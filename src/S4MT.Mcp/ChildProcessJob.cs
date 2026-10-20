using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace S4MT.Mcp;

internal sealed class ChildProcessJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectBasicAccountingInformation = 1;
    private const uint ChildTerminationExitCode = 130;
    private const int PollDelayMilliseconds = 10;
    private const int CleanupTimeoutMilliseconds = 5000;
    private const int GracefulStopMilliseconds = 3000;
    private const int SignalTerm = 15;
    private const int SignalKill = 9;
    private const int ErrorNoSuchProcess = 3;

    private static ChildProcessJob? hostLifetime;
    private readonly SafeFileHandle? handle;
    private int? processGroupId;
    internal SafeFileHandle? Handle => handle;

    internal static void ContainHostLifetime()
    {
        if (!OperatingSystem.IsWindows()) return;
        hostLifetime = new ChildProcessJob();
        using var host = Process.GetCurrentProcess();
        hostLifetime.Attach(host);
    }

    internal ChildProcessJob()
    {
        if (!OperatingSystem.IsWindows()) return;
        var created = CreateJobObjectW(IntPtr.Zero, null);
        if (created.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            created.Dispose();
            throw new McpFailure("cli_unavailable", $"Could not create child process job: {new Win32Exception(error).Message}");
        }
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = JobObjectLimitKillOnJobClose } };
        if (!SetInformationJobObject(created, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = Marshal.GetLastWin32Error();
            created.Dispose();
            throw new McpFailure("cli_unavailable", $"Could not configure child process job: {new Win32Exception(error).Message}");
        }
        handle = created;
    }

    internal void Attach(Process child)
    {
        if (OperatingSystem.IsLinux())
        {
            var group = GetProcessGroupId(child.Id);
            if (group != child.Id)
            {
                var detail = group < 0
                    ? new Win32Exception(Marshal.GetLastPInvokeError()).Message
                    : $"the process belongs to unexpected group {group}";
                throw new McpFailure("cli_unavailable", $"Could not isolate the Linux CLI process group: {detail}.");
            }
            processGroupId = group;
            return;
        }
        if (handle is not null && !AssignProcessToJobObject(handle, child.Handle))
            throw new McpFailure("cli_unavailable", $"Could not contain CLI process: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
    }

    internal async Task StopAsync(Process child)
    {
        if (OperatingSystem.IsLinux())
        {
            await StopLinuxProcessGroupAsync(child, processGroupId
                ?? throw new McpFailure("cleanup_failed", "The Linux CLI process group was not recorded."));
            return;
        }
        if (handle is null)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            return;
        }
        if (!TerminateJobObject(handle, ChildTerminationExitCode))
            throw new McpFailure("cleanup_failed", "Could not terminate the CLI process job.");
        using var deadline = new CancellationTokenSource(CleanupTimeoutMilliseconds);
        while (true)
        {
            if (!QueryInformationJobObject(handle, JobObjectBasicAccountingInformation, out var accounting, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero))
                throw new McpFailure("cleanup_failed", "Could not inspect the CLI process job.");
            if (accounting.ActiveProcesses == 0) return;
            try { await Task.Delay(PollDelayMilliseconds, deadline.Token); }
            catch (OperationCanceledException) { throw new McpFailure("cleanup_failed", "CLI process job did not stop within five seconds."); }
        }
    }

    private static async Task StopLinuxProcessGroupAsync(Process child, int group)
    {
        SignalGroup(group, SignalTerm);
        var gracefulDeadline = DateTime.UtcNow.AddMilliseconds(GracefulStopMilliseconds);
        while (DateTime.UtcNow < gracefulDeadline)
        {
            if (child.HasExited) await child.WaitForExitAsync();
            if (!LinuxProcessGroupExists(group)) return;
            await Task.Delay(PollDelayMilliseconds);
        }

        SignalGroup(group, SignalKill);
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(CleanupTimeoutMilliseconds)); }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or Win32Exception)
        {
            throw new McpFailure("cleanup_failed", $"Could not confirm Linux CLI process exit: {exception.Message}");
        }

        var cleanupDeadline = DateTime.UtcNow.AddMilliseconds(CleanupTimeoutMilliseconds);
        while (LinuxProcessGroupExists(group))
        {
            if (DateTime.UtcNow >= cleanupDeadline)
                throw new McpFailure("cleanup_failed", "Linux CLI process group did not stop within five seconds.");
            await Task.Delay(PollDelayMilliseconds);
        }
    }

    private static void SignalGroup(int group, int signal)
    {
        if (SendSignal(-group, signal) == 0) return;
        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorNoSuchProcess) return;
        throw new McpFailure("cleanup_failed", $"Could not signal the Linux CLI process group: {new Win32Exception(error).Message}");
    }

    private static bool LinuxProcessGroupExists(int group)
    {
        if (SendSignal(-group, 0) == 0) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorNoSuchProcess) return false;
        throw new McpFailure("cleanup_failed", $"Could not inspect the Linux CLI process group: {new Win32Exception(error).Message}");
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

    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static extern int GetProcessGroupId(int processId);
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SendSignal(int processId, int signal);
}
