using System.Text.Json;
using System.Text.Json.Serialization;

namespace S4MT.Mcp;

internal sealed record HostOptions
{
    public required string CliPath { get; init; }
    public string? DotnetPath { get; init; }
    public required string[] ReadRoots { get; init; }
    public required string[] WriteRoots { get; init; }
    public required string[] GameRoots { get; init; }
    public int TimeoutMilliseconds { get; init; } = 30_000;
    public int MaximumStdoutBytes { get; init; } = 1_048_576;
    public int MaximumStderrBytes { get; init; } = 65_536;

    public static HostOptions Load(string path)
    {
        var options = JsonSerializer.Deserialize<HostOptions>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? throw new ArgumentException("Configuration must be a JSON object.");
        options.Validate();
        return options;
    }

    internal void Validate()
    {
        Absolute(CliPath);
        if (Path.GetExtension(CliPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            Absolute(DotnetPath);
            if (OperatingSystem.IsWindows() && !Path.GetExtension(DotnetPath!).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("dotnetPath must name a .exe executable on Windows.");
        }
        else if (OperatingSystem.IsWindows() && !Path.GetExtension(CliPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("cliPath must name a CLI executable or DLL.");
        if (ReadRoots is not { Length: > 0 } || WriteRoots is not { Length: > 0 } || GameRoots is null)
            throw new ArgumentException("Configure readRoots, writeRoots, and gameRoots explicitly. Read and write roots cannot be empty.");
        foreach (var root in ReadRoots.Concat(WriteRoots).Concat(GameRoots)) Absolute(root);
        if (TimeoutMilliseconds is < 1 or > 600_000 ||
            MaximumStdoutBytes is < 256 or > 16_777_216 || MaximumStderrBytes is < 256 or > 1_048_576)
            throw new ArgumentException("Invalid timeout or output limits.");
    }

    internal static string Absolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Contains('\0'))
            throw new McpFailure("invalid_arguments", "Paths must be explicit absolute paths.");
        return Path.GetFullPath(path);
    }
}

internal sealed class McpFailure(string code, string message, int? exitCode = null, string? stderr = null) : Exception(message)
{
    public string Code { get; } = code;
    public int? ExitCode { get; } = exitCode;
    public string? Stderr { get; } = stderr;
}
