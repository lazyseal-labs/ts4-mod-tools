using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace S4MT.Cli;

internal static class PathGuard
{
    public static void EnsureWithinDirectory(string path, string directoryPath, string description)
    {
        var fullPath = ResolvePath(path);
        var fullDirectoryPath = ResolvePath(directoryPath);
        if (!IsWithinDirectory(fullDirectoryPath, fullPath))
            throw new CliInputException($"{description} escapes its directory: {path}");
    }

    public static void EnsureOutsideDirectory(string outputPath, string directoryPath, string description)
    {
        var fullOutputPath = ResolvePath(outputPath);
        var fullDirectoryPath = ResolvePath(directoryPath);
        if (IsWithinDirectory(fullDirectoryPath, fullOutputPath))
            throw new CliInputException($"Output path must be outside {description}: {directoryPath}");
    }

    public static void EnsureDoesNotOverwrite(
        string outputPath,
        params (string Path, string Description)[] inputs)
    {
        var fullOutputPath = ResolvePath(outputPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (var (inputPath, description) in inputs)
        {
            if (string.Equals(fullOutputPath, ResolvePath(inputPath), comparison))
                throw new CliInputException($"Output path must not overwrite its {description}.");
        }
    }

    private static string ResolvePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new CliInputException($"Could not determine the root of path: {path}");
        var current = root;
        var components = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var component in components)
        {
            var candidate = Path.Combine(current, component);
            var isDirectory = Directory.Exists(candidate);
            if (!isDirectory && !File.Exists(candidate))
            {
                current = candidate;
                continue;
            }

            current = OperatingSystem.IsWindows()
                ? ResolveWindowsFinalPath(candidate)
                : ResolveLink(candidate, isDirectory);
        }

        return Path.GetFullPath(current);
    }

    private static string ResolveLink(string path, bool isDirectory)
    {
        FileSystemInfo fileSystemInfo = isDirectory
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        var target = fileSystemInfo.ResolveLinkTarget(returnFinalTarget: true);
        return target is null ? path : Path.GetFullPath(target.FullName);
    }

    private static string ResolveWindowsFinalPath(string path)
    {
        using var handle = CreateFileW(
            path,
            FileReadAttributes,
            ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw new CliInputException(
                $"Could not resolve path safely: {path} ({new Win32Exception(Marshal.GetLastWin32Error()).Message})");

        var capacity = 512;
        while (capacity <= 32768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)capacity, flags: 0);
            if (length == 0)
                throw new CliInputException(
                    $"Could not resolve path safely: {path} ({new Win32Exception(Marshal.GetLastWin32Error()).Message})");
            if (length < capacity)
                return RemoveDevicePrefix(buffer.ToString());
            capacity = checked((int)length + 1);
        }

        throw new CliInputException($"Resolved path is too long: {path}");
    }

    private static string RemoveDevicePrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[uncPrefix.Length..];

        const string devicePrefix = @"\\?\";
        if (path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase) &&
            path.Length >= devicePrefix.Length + 2 &&
            char.IsAsciiLetter(path[devicePrefix.Length]) &&
            path[devicePrefix.Length + 1] == ':')
            return path[devicePrefix.Length..];

        return path;
    }

    private const uint FileReadAttributes = 0x80;
    private const uint ShareRead = 0x1;
    private const uint ShareWrite = 0x2;
    private const uint ShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    private static bool IsWithinDirectory(string directoryPath, string path)
    {
        var relativePath = Path.GetRelativePath(directoryPath, path);
        return !Path.IsPathRooted(relativePath) &&
               relativePath != ".." &&
               !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }
}
