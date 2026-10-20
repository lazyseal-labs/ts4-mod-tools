using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace S4MT.Mcp;

internal sealed class CliChildProcess : IDisposable
{
    private const uint HandleFlagInherit = 0x00000001;
    private const uint StartupInfoUseStdHandles = 0x00000100;
    private const uint CreateNoWindow = 0x08000000;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateSuspended = 0x00000004;
    private const uint SuspendedLaunchExitCode = 130;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeoutMilliseconds = 5000;
    private const uint ExpectedPreviousSuspendCount = 1;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    private const nuint ProcThreadAttributeJobList = 0x0002000D;
    private const string LinuxStartGate = "IFS= read -r _ || exit 130; exec \"$@\"";

    internal Process Process { get; }
    internal Stream StandardOutput { get; }
    internal Stream StandardError { get; }
    private readonly SafeFileHandle? initialThread;
    private Stream? startGate;

    private CliChildProcess(Process process, Stream stdout, Stream stderr, SafeFileHandle? initialThread, Stream? startGate = null)
    {
        Process = process;
        StandardOutput = stdout;
        StandardError = stderr;
        this.initialThread = initialThread;
        this.startGate = startGate;
    }

    internal static CliChildProcess Start(ProcessStartInfo start, ChildProcessJob job, Func<int, Process> processResolver)
    {
        if (OperatingSystem.IsWindows()) return StartWindows(start, job.Handle!, processResolver);
        if (OperatingSystem.IsLinux()) return StartLinux(start, job);
        throw new McpFailure("cli_unavailable", "Contained CLI execution is currently supported on Windows and Linux.");
    }

    internal void Resume()
    {
        if (initialThread is null)
        {
            var gate = startGate ?? throw new InvalidOperationException("Linux CLI start gate was already released.");
            gate.WriteByte((byte)'\n');
            gate.Flush();
            gate.Dispose();
            startGate = null;
            return;
        }
        var previousSuspendCount = ResumeThread(initialThread);
        if (previousSuspendCount == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        RequireSingleResume(previousSuspendCount);
        initialThread.Dispose();
    }

    private static CliChildProcess StartLinux(ProcessStartInfo start, ChildProcessJob job)
    {
        var setsid = File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid" : "/bin/setsid";
        if (!File.Exists(setsid))
            throw new McpFailure("cli_unavailable", "Linux CLI execution requires the setsid utility from util-linux.");

        var launch = new ProcessStartInfo(setsid)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = start.WorkingDirectory
        };
        launch.ArgumentList.Add("/bin/sh");
        launch.ArgumentList.Add("-c");
        launch.ArgumentList.Add(LinuxStartGate);
        launch.ArgumentList.Add("s4mt-cli-launch");
        launch.ArgumentList.Add(start.FileName);
        foreach (var argument in start.ArgumentList) launch.ArgumentList.Add(argument);

        var process = new Process { StartInfo = launch };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Could not start the Linux CLI launcher.");
            WaitForIsolatedProcessGroup(process);
            job.Attach(process);
            return new CliChildProcess(process, process.StandardOutput.BaseStream, process.StandardError.BaseStream,
                initialThread: null, process.StandardInput.BaseStream);
        }
        catch
        {
            if (!process.HasExited)
            {
                try { job.Attach(process); job.StopAsync(process).GetAwaiter().GetResult(); }
                catch { process.Kill(entireProcessTree: true); }
                try { process.WaitForExit(5000); } catch (InvalidOperationException) { }
            }
            process.Dispose();
            throw;
        }
    }

    private static void WaitForIsolatedProcessGroup(Process process)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (process.HasExited)
                throw new InvalidOperationException("The Linux CLI launcher exited before establishing its process group.");
            var group = GetProcessGroupId(process.Id);
            if (group == process.Id) return;
            Thread.Sleep(10);
        }
        throw new InvalidOperationException("The Linux CLI launcher did not establish an isolated process group.");
    }

    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static extern int GetProcessGroupId(int processId);

    internal static void RequireSingleResume(uint previousSuspendCount)
    {
        if (previousSuspendCount != ExpectedPreviousSuspendCount)
            throw new InvalidOperationException($"CLI primary thread had an unexpected suspend count: {previousSuspendCount}.");
    }

    private static CliChildProcess StartWindows(ProcessStartInfo start, SafeFileHandle job, Func<int, Process> processResolver)
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = true };
        if (!CreatePipe(out var stdoutRead, out var stdoutWrite, ref security, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (stdoutWrite)
        {
            if (!CreatePipe(out var stderrRead, out var stderrWrite, ref security, 0))
            {
                stdoutRead.Dispose();
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            using (stderrWrite)
            {
                if (!CreatePipe(out var stdinRead, out var stdinWrite, ref security, 0))
                {
                    stdoutRead.Dispose();
                    stderrRead.Dispose();
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                using (stdinRead)
                using (stdinWrite)
                {
                    var transferred = false;
                    IntPtr attributes = IntPtr.Zero, inheritedHandles = IntPtr.Zero, jobs = IntPtr.Zero;
                    var initialized = false;
                    try
                    {
                        if (!SetHandleInformation(stdinWrite, HandleFlagInherit, 0) ||
                            !SetHandleInformation(stdoutRead, HandleFlagInherit, 0) ||
                            !SetHandleInformation(stderrRead, HandleFlagInherit, 0))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        nuint size = 0;
                        InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
                        attributes = Marshal.AllocHGlobal(checked((int)size));
                        if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
                        initialized = true;
                        inheritedHandles = Marshal.AllocHGlobal(3 * IntPtr.Size);
                        Marshal.WriteIntPtr(inheritedHandles, 0, stdinRead.DangerousGetHandle());
                        Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size, stdoutWrite.DangerousGetHandle());
                        Marshal.WriteIntPtr(inheritedHandles, 2 * IntPtr.Size, stderrWrite.DangerousGetHandle());
                        jobs = Marshal.AllocHGlobal(IntPtr.Size);
                        Marshal.WriteIntPtr(jobs, job.DangerousGetHandle());
                        if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, inheritedHandles, (nuint)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero) ||
                            !UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeJobList, jobs, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        var startup = new StartupInfoEx
                        {
                            StartupInfo = new StartupInfo
                            {
                                Size = Marshal.SizeOf<StartupInfoEx>(),
                                Flags = StartupInfoUseStdHandles,
                                StandardInput = stdinRead.DangerousGetHandle(),
                                StandardOutput = stdoutWrite.DangerousGetHandle(),
                                StandardError = stderrWrite.DangerousGetHandle()
                            },
                            AttributeList = attributes
                        };
                        var commandLine = new StringBuilder(string.Join(' ', new[] { start.FileName }.Concat(start.ArgumentList).Select(QuoteWindowsArgument)));
                        if (!CreateProcessW(start.FileName, commandLine, IntPtr.Zero, IntPtr.Zero, true,
                                CreateNoWindow | ExtendedStartupInfoPresent | CreateSuspended, IntPtr.Zero, start.WorkingDirectory, ref startup, out var information))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        using var processHandle = new SafeFileHandle(information.Process, ownsHandle: true);
                        var thread = new SafeFileHandle(information.Thread, ownsHandle: true);
                        Process? process = null;
                        FileStream? standardOutput = null;
                        FileStream? standardError = null;
                        try
                        {
                            process = processResolver(checked((int)information.ProcessId));
                            _ = process.Handle;
                            standardOutput = new FileStream(stdoutRead, FileAccess.Read);
                            standardError = new FileStream(stderrRead, FileAccess.Read);
                            var result = new CliChildProcess(process, standardOutput, standardError, thread);
                            transferred = true;
                            return result;
                        }
                        catch
                        {
                            standardError?.Dispose();
                            standardOutput?.Dispose();
                            process?.Dispose();
                            thread.Dispose();
                            TerminateSuspendedProcess(processHandle);
                            throw;
                        }
                    }
                    finally
                    {
                        if (initialized) DeleteProcThreadAttributeList(attributes);
                        Marshal.FreeHGlobal(attributes);
                        Marshal.FreeHGlobal(inheritedHandles);
                        Marshal.FreeHGlobal(jobs);
                        if (!transferred) { stdoutRead.Dispose(); stderrRead.Dispose(); }
                    }
                }
            }
        }
    }

    private static void TerminateSuspendedProcess(SafeFileHandle process)
    {
        if (!TerminateProcess(process, SuspendedLaunchExitCode) && WaitForSingleObject(process, 0) != WaitObject0)
            throw new McpFailure("cleanup_failed", "Could not terminate the suspended CLI after launch setup failed.");
        if (WaitForSingleObject(process, WaitTimeoutMilliseconds) != WaitObject0)
            throw new McpFailure("cleanup_failed", "Could not confirm termination of the suspended CLI after launch setup failed.");
    }

    private static string QuoteWindowsArgument(string argument)
    {
        if (argument.Contains('\0')) throw new ArgumentException("CLI arguments cannot contain NUL.");
        // Match the backslash/quote rules used by Microsoft C startup code, including doubling trailing slashes before the closing quote.
        // https://learn.microsoft.com/en-us/cpp/c-language/parsing-c-command-line-arguments?view=vs-2019
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            quoted.Append('\\', character == '"' ? 2 * slashes + 1 : slashes);
            quoted.Append(character);
            slashes = 0;
        }
        return quoted.Append('\\', 2 * slashes).Append('"').ToString();
    }

    public void Dispose()
    {
        startGate?.Dispose();
        initialThread?.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        Process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XCharacters, YCharacters, FillAttribute, Flags;
        public ushort ShowWindow, ReservedBytes;
        public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, uint count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, nuint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
}
