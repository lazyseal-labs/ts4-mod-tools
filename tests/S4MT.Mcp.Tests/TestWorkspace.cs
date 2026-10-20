using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using S4MT.Mcp;

namespace S4MT.Mcp.Tests;

internal sealed class TestWorkspace : IDisposable
{
    private readonly List<string> links = [];
    internal static string RepositoryRoot
    {
        get
        {
            var path = new DirectoryInfo(AppContext.BaseDirectory);
            while (path is not null && !File.Exists(Path.Combine(path.FullName, "S4MT.sln"))) path = path.Parent;
            return path?.FullName ?? throw new InvalidOperationException("Repository not found.");
        }
    }

    internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"s4mt-mcp-test-{Guid.NewGuid():N}");
    internal string Cli { get; }
    internal string Dotnet { get; } = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
        Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
    internal string Host { get; }
    internal string Configuration { get; }
    internal HostOptions Options { get; }

    internal TestWorkspace(string mode = "success", int timeout = 5000, int outputLimit = 65536)
    {
        Directory.CreateDirectory(Root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var fixture = Path.Combine(RepositoryRoot, "tests", "S4MT.Mcp.Tests", "Fixture", "bin", configuration, "net10.0");
        foreach (var file in Directory.GetFiles(fixture)) File.Copy(file, Path.Combine(Root, Path.GetFileName(file)));
        Cli = Path.Combine(Root, "Fixture.dll");
        Host = Path.Combine(RepositoryRoot, "src", "S4MT.Mcp", "bin", configuration, "net10.0", "S4MT.Mcp.dll");
        File.WriteAllText(Path.Combine(Root, "mode.txt"), mode);
        Options = new HostOptions
        {
            CliPath = Cli,
            DotnetPath = Dotnet,
            ReadRoots = [Root],
            WriteRoots = [Root],
            GameRoots = [Path.Combine(Root, "game")],
            TimeoutMilliseconds = timeout,
            MaximumStdoutBytes = outputLimit,
            MaximumStderrBytes = outputLimit
        };
        Configuration = Path.Combine(Root, "config.json");
        SaveConfig();
    }

    internal void SaveConfig(HostOptions? options = null) => File.WriteAllText(Configuration,
        JsonSerializer.Serialize(options ?? Options, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    internal string Project(string name = "project", string? manifestName = null)
    {
        var root = Path.Combine(Root, name);
        Directory.CreateDirectory(root);
        var manifest = manifestName ?? "package.json";
        File.WriteAllText(Path.Combine(root, "s4mt.json"), JsonSerializer.Serialize(new { format = 1, name = "TestProject", packageManifest = manifest, scriptArchives = Array.Empty<string>() }));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, manifest))!);
        File.WriteAllText(Path.Combine(root, manifest), "{\"format\":1,\"resources\":[]}");
        return root;
    }

    internal async Task<int> WaitForPid(string file)
    {
        var path = Path.Combine(Root, file);
        using var deadline = new CancellationTokenSource(5000);
        while (!File.Exists(path)) await Task.Delay(20, deadline.Token);
        return int.Parse(await File.ReadAllTextAsync(path, deadline.Token));
    }

    internal static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    internal void DirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            links.Add(link);
            return;
        }
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException(process.StandardError.ReadToEnd());
        links.Add(link);
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(Root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("s4mt-mcp-test-"))
            throw new InvalidOperationException("Unsafe fixture cleanup.");
        foreach (var link in links.AsEnumerable().Reverse()) Directory.Delete(link);
        Directory.Delete(full, recursive: true);
    }
}
