using System.Net;
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
    public string Transport { get; init; } = "stdio";
    public string HttpListenUrl { get; init; } = "http://127.0.0.1:8080";
    public bool AllowPrivateNetworkHttp { get; init; }
    public string? HttpBearerTokenFile { get; init; }
    public string? CacheAdminBearerTokenFile { get; init; }
    public string? GameIndexCacheRoot { get; init; }
    public long MaximumGameIndexBytes { get; init; } = 536_870_912;
    public long MaximumGameIndexUncompressedBytes { get; init; } = 8_589_934_592;
    public long MaximumRetainedGameIndexBytes { get; init; } = 4_294_967_296;
    public int MaximumGameIndexImportMilliseconds { get; init; } = 1_800_000;
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
        if (Transport is not ("stdio" or "http"))
            throw new ArgumentException("transport must be either 'stdio' or 'http'.");
        if (Transport == "http")
        {
            if (!Uri.TryCreate(HttpListenUrl, UriKind.Absolute, out var listenUri) ||
                listenUri.Scheme is not ("http" or "https") || listenUri.Port is < 1 or > 65535)
                throw new ArgumentException("httpListenUrl must be an absolute HTTP or HTTPS URL with a valid port.");
            if (listenUri.Scheme == "http" && !listenUri.IsLoopback &&
                (!AllowPrivateNetworkHttp || !IsPrivateNetworkAddress(listenUri)))
                throw new ArgumentException("Plain HTTP must bind to loopback. For private-network access, set allowPrivateNetworkHttp to true and bind to a specific private IP address.");
            Absolute(HttpBearerTokenFile);
            Absolute(CacheAdminBearerTokenFile);
            Absolute(GameIndexCacheRoot);
            var tokenPath = Path.GetFullPath(HttpBearerTokenFile!);
            var adminTokenPath = Path.GetFullPath(CacheAdminBearerTokenFile!);
            var cacheRoot = Path.GetFullPath(GameIndexCacheRoot!);
            var configuredRoots = ReadRoots.Concat(WriteRoots).Concat(GameRoots).ToArray();
            if (configuredRoots.Any(root => PathPolicy.Within(cacheRoot, root) || PathPolicy.Within(root, cacheRoot)))
                throw new ArgumentException("gameIndexCacheRoot must be disjoint from readRoots, writeRoots, and gameRoots.");
            if (tokenPath.Equals(adminTokenPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ArgumentException("httpBearerTokenFile and cacheAdminBearerTokenFile must be separate files.");
            foreach (var tokenFile in new[] { tokenPath, adminTokenPath })
                if (configuredRoots.Any(root => PathPolicy.Within(tokenFile, root) || PathPolicy.Within(root, tokenFile)) ||
                    PathPolicy.Within(tokenFile, cacheRoot) || PathPolicy.Within(cacheRoot, tokenFile))
                    throw new ArgumentException("Bearer token files must be outside readRoots, writeRoots, gameRoots, and gameIndexCacheRoot.");
            if (MaximumGameIndexBytes is < 1_024 or > 2_147_483_647)
                throw new ArgumentException("maximumGameIndexBytes must be between 1024 and 2147483647.");
            if (MaximumGameIndexUncompressedBytes is < 1_024 or > 1_099_511_627_776 ||
                MaximumRetainedGameIndexBytes is < 1_024 or > 1_099_511_627_776)
                throw new ArgumentException("Game-index expanded and retained size limits must be between 1024 bytes and 1 TiB.");
            if (MaximumGameIndexImportMilliseconds is < 1_000 or > 3_600_000)
                throw new ArgumentException("maximumGameIndexImportMilliseconds must be between 1000 and 3600000.");
        }
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

    private static bool IsPrivateNetworkAddress(Uri uri)
    {
        if (uri.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            !IPAddress.TryParse(uri.Host, out var address))
            return false;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            return bytes[0] == 10 ||
                (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
                (bytes[0] == 192 && bytes[1] == 168);

        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 &&
            (bytes[0] & 0xfe) == 0xfc;
    }
}

internal sealed class McpFailure(string code, string message, int? exitCode = null, string? stderr = null) : Exception(message)
{
    public string Code { get; } = code;
    public int? ExitCode { get; } = exitCode;
    public string? Stderr { get; } = stderr;
}
