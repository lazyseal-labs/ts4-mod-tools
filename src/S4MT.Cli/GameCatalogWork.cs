using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlamaLogic.Packages;

namespace S4MT.Cli;

internal sealed class GameCatalogWork
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GameIndexBuildReport> IndexAsync(
        string gameRootPath,
        string indexPath,
        CancellationToken cancellationToken)
    {
        var gameRoot = Path.GetFullPath(gameRootPath);
        if (!Directory.Exists(gameRoot))
            throw new CliInputException($"Game directory does not exist: {gameRoot}");

        var outputPath = Path.GetFullPath(indexPath);
        PathGuard.EnsureOutsideDirectory(outputPath, gameRoot, "the game directory");

        var packages = DiscoverPackages(gameRoot);
        if (packages.Count == 0)
            throw new CliInputException($"No .package files were found under: {gameRoot}");

        var gameVersion = GetGameVersion(gameRoot);
        var fingerprint = ComputeFingerprint(gameRoot, gameVersion, packages);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        if (File.Exists(outputPath))
        {
            try
            {
                var existing = await ReadIndexAsync(outputPath, expectedTgi: null, cancellationToken);
                if (existing.Header.Format == 2 &&
                    string.Equals(existing.Header.Fingerprint, fingerprint, StringComparison.Ordinal) &&
                    PathsEqual(existing.Header.GameRoot, gameRoot) &&
                    string.Equals(existing.Header.GameVersion, gameVersion, StringComparison.Ordinal) &&
                    existing.Header.PackageCount == packages.Count)
                {
                    return new GameIndexBuildReport(
                        outputPath,
                        gameRoot,
                        gameVersion,
                        packages.Count,
                        existing.ResourceCount,
                        fingerprint,
                        Reused: true);
                }
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or FormatException or InvalidDataException)
            {
            }
        }

        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            long resourceCount = 0;
            await using (var file = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            await using (var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true))
            await using (var writer = new StreamWriter(
                gzip, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 64 * 1024, leaveOpen: true))
            using (var writeGate = new SemaphoreSlim(1, 1))
            {
                var options = new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 4)
                };

                await Parallel.ForEachAsync(packages, options, async (source, token) =>
                {
                    try
                    {
                        var lines = await ReadPackageRowsAsync(source, token);
                        await writeGate.WaitAsync(token);
                        try
                        {
                            await writer.WriteAsync(lines.AsMemory(), token);
                        }
                        finally
                        {
                            writeGate.Release();
                        }

                        Interlocked.Add(ref resourceCount, source.ResourceCount);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        throw new InvalidDataException(
                            $"Could not index package '{source.RelativePath}': {exception.Message}", exception);
                    }
                });

                var header = new IndexHeader
                {
                    RecordType = "header",
                    Format = 2,
                    GameRoot = gameRoot,
                    GameVersion = gameVersion,
                    Fingerprint = fingerprint,
                    PackageCount = packages.Count,
                    ResourceCount = resourceCount,
                    GeneratedUtc = DateTimeOffset.UtcNow,
                    PrecedenceVerified = false
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(header, JsonOptions));
                await writer.FlushAsync(cancellationToken);
            }

            var packagesAfterScan = DiscoverPackages(gameRoot);
            var fingerprintAfterScan = ComputeFingerprint(gameRoot, GetGameVersion(gameRoot), packagesAfterScan);
            if (!string.Equals(fingerprint, fingerprintAfterScan, StringComparison.Ordinal))
                throw new IOException("Game package files changed during indexing; the old index was preserved.");

            File.Move(temporaryPath, outputPath, overwrite: true);
            return new GameIndexBuildReport(
                outputPath,
                gameRoot,
                gameVersion,
                packages.Count,
                resourceCount,
                fingerprint,
                Reused: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<GameQueryReport> QueryAsync(
        string indexPath,
        ResourceId resourceId,
        CancellationToken cancellationToken)
    {
        var fullIndexPath = Path.GetFullPath(indexPath);
        var index = await ReadIndexAsync(fullIndexPath, resourceId.ToString(), cancellationToken);
        var entries = index.Matches
            .OrderByDescending(provider => provider.LayerPriority)
            .ThenBy(provider => provider.Source, StringComparer.Ordinal)
            .ToArray();
        var providers = entries.Select(entry => new GameIndexProvider(
            entry.Source,
            entry.Layer,
            entry.Pack,
            entry.LayerPriority,
            entry.PackageSizeBytes,
            entry.PackageModifiedUtc)).ToArray();
        var resolution = providers.Length switch
        {
            0 => "not_found",
            1 => "single_provider",
            _ => "multiple_providers"
        };

        return new GameQueryReport(
            fullIndexPath,
            resourceId.ToString(),
            resolution,
            index.Header.PrecedenceVerified,
            providers.Length == 1 ? providers[0] : null,
            providers,
            providers.Length > 1
                ? "Directory tiers are only a search hint. Multiple providers are returned because game load precedence has not been verified."
                : null);
    }

    public async Task<GameFindReport> FindByTypeAsync(
        string indexPath,
        uint type,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 1000)
            throw new CliInputException("Game find limit must be between 1 and 1000.");

        var fullIndexPath = Path.GetFullPath(indexPath);
        await using var file = File.OpenRead(fullIndexPath);
        await using var gzip = new GZipStream(file, System.IO.Compression.CompressionMode.Decompress, leaveOpen: false);
        using var reader = new StreamReader(gzip, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var prefix = $"0x{type:X8}:";
        var resources = new List<GameFindResource>(limit);
        IndexHeader? header = null;
        long resourceCount = 0;
        long matchCount = 0;
        string? line;
        var headerSeen = false;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                throw new InvalidDataException("Game index contains an empty record.");

            using var document = JsonDocument.Parse(line);
            var recordType = document.RootElement.GetProperty("recordType").GetString();
            if (recordType == "header")
            {
                if (headerSeen)
                    throw new InvalidDataException("Game index contains multiple headers.");
                header = document.RootElement.Deserialize<IndexHeader>(JsonOptions)
                    ?? throw new InvalidDataException("Game index header is empty.");
                headerSeen = true;
                continue;
            }

            if (recordType != "resource" || headerSeen)
                throw new InvalidDataException("Game index record order or type is invalid.");

            var entry = document.RootElement.Deserialize<IndexEntry>(JsonOptions)
                ?? throw new InvalidDataException("Game index resource record is empty.");
            resourceCount++;
            var tgi = entry.Tgi;
            if (!tgi.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            matchCount++;
            if (resources.Count < limit)
                resources.Add(new GameFindResource(entry.Tgi, entry.Source, entry.Layer, entry.Pack, entry.LayerPriority));
        }

        if (header is null || header.Format != 2 || header.ResourceCount != resourceCount)
            throw new InvalidDataException("Game index header is missing or does not match its resource records.");

        return new GameFindReport(
            fullIndexPath,
            type,
            matchCount,
            limit,
            matchCount > resources.Count,
            header.PrecedenceVerified,
            header.GameVersion,
            header.Fingerprint,
            header.PackageCount,
            resourceCount,
            header.GeneratedUtc,
            resources);
    }

    public async Task<GameProviderLookupReport> LookupProvidersAsync(
        string indexPath,
        IReadOnlySet<string> tgis,
        CancellationToken cancellationToken,
        IReadOnlySet<uint>? requestedTypes = null)
    {
        var fullIndexPath = Path.GetFullPath(indexPath);
        await using var file = File.OpenRead(fullIndexPath);
        await using var gzip = new GZipStream(file, System.IO.Compression.CompressionMode.Decompress, leaveOpen: false);
        using var reader = new StreamReader(gzip, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var providers = new Dictionary<string, List<GameIndexProvider>>(StringComparer.OrdinalIgnoreCase);
        var observedTypes = new HashSet<uint>();
        IndexHeader? header = null;
        long resourceCount = 0;
        string? line;
        var headerSeen = false;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                throw new InvalidDataException("Game index contains an empty record.");

            using var document = JsonDocument.Parse(line);
            var recordType = document.RootElement.GetProperty("recordType").GetString();
            if (recordType == "header")
            {
                if (headerSeen)
                    throw new InvalidDataException("Game index contains multiple headers.");
                header = document.RootElement.Deserialize<IndexHeader>(JsonOptions)
                    ?? throw new InvalidDataException("Game index header is empty.");
                headerSeen = true;
                continue;
            }

            if (recordType != "resource" || headerSeen)
                throw new InvalidDataException("Game index record order or type is invalid.");

            resourceCount++;
            var tgi = document.RootElement.GetProperty("tgi").GetString();
            if (tgi is null)
                continue;
            if (requestedTypes is { Count: > 0 } && tgi.Length >= 10 &&
                uint.TryParse(tgi.AsSpan(2, 8), System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out var type) && requestedTypes.Contains(type))
                observedTypes.Add(type);
            if (!tgis.Contains(tgi))
                continue;

            var entry = document.RootElement.Deserialize<IndexEntry>(JsonOptions)
                ?? throw new InvalidDataException("Game index resource record is empty.");
            if (!providers.TryGetValue(entry.Tgi, out var matches))
                providers.Add(entry.Tgi, matches = []);
            matches.Add(new GameIndexProvider(
                entry.Source,
                entry.Layer,
                entry.Pack,
                entry.LayerPriority,
                entry.PackageSizeBytes,
                entry.PackageModifiedUtc));
        }

        if (header is null || header.Format != 2 || header.ResourceCount != resourceCount)
            throw new InvalidDataException("Game index header is missing or does not match its resource records.");

        var orderedProviders = providers.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<GameIndexProvider>)pair.Value
                .OrderByDescending(provider => provider.LayerPriority)
                .ThenBy(provider => provider.Source, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
        return new GameProviderLookupReport(fullIndexPath, resourceCount, header.PrecedenceVerified,
            orderedProviders, header.Fingerprint, header.GameVersion, observedTypes);
    }

    private static async Task<string> ReadPackageRowsAsync(
        PackageFile source,
        CancellationToken cancellationToken)
    {
        await using var package = await DataBasePackedFile.FromPathAsync(source.FullPath);
        var keys = await package.GetKeysAsync(cancellationToken: cancellationToken);
        var output = new StringBuilder(keys.Count * 128);

        foreach (var key in keys.OrderBy(
                     key => new ResourceId(unchecked((uint)key.Type), key.Group, key.FullInstance).ToString(),
                     StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resourceId = new ResourceId(unchecked((uint)key.Type), key.Group, key.FullInstance);
            var entry = new IndexEntry
            {
                RecordType = "resource",
                Tgi = resourceId.ToString(),
                Source = source.RelativePath,
                Layer = source.Layer,
                Pack = source.Pack,
                LayerPriority = source.LayerPriority,
                PackageSizeBytes = source.SizeBytes,
                PackageModifiedUtc = source.LastWriteUtc
            };
            output.Append(JsonSerializer.Serialize(entry, JsonOptions)).Append('\n');
        }

        source.ResourceCount = keys.Count;
        return output.ToString();
    }

    private static List<PackageFile> DiscoverPackages(string gameRoot)
    {
        var packages = new List<PackageFile>();
        foreach (var fullPath in Directory.EnumerateFiles(gameRoot, "*.package", SearchOption.AllDirectories))
        {
            var file = new FileInfo(fullPath);
            var relativePath = Path.GetRelativePath(gameRoot, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var (layer, pack, priority) = ClassifyLayer(segments);
            packages.Add(new PackageFile(
                fullPath,
                relativePath,
                layer,
                pack,
                priority,
                file.Length,
                file.LastWriteTimeUtc));
        }

        packages.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
        return packages;
    }

    private static (string Layer, string Pack, int Priority) ClassifyLayer(string[] segments)
    {
        if (segments.Length == 0)
            return ("unknown", "unknown", 0);

        var root = segments[0];
        var fileName = segments[^1];
        if (fileName.Contains("DeltaBuild", StringComparison.OrdinalIgnoreCase))
            return ("patch", Path.GetFileNameWithoutExtension(fileName), 300);
        if (string.Equals(root, "Data", StringComparison.OrdinalIgnoreCase))
            return ("base", "base", 100);
        if (string.Equals(root, "Delta", StringComparison.OrdinalIgnoreCase))
            return ("patch", segments.Length > 1 ? segments[1] : "Delta", 300);
        if (IsPackDirectory(root))
            return ("pack", root, 200);
        return ("other", root, 0);
    }

    private static bool IsPackDirectory(string value) =>
        value.Length == 4 &&
        value[0] is 'E' or 'e' or 'G' or 'g' or 'S' or 's' or 'F' or 'f' &&
        value[1] is 'P' or 'p' &&
        char.IsAsciiDigit(value[2]) &&
        char.IsAsciiDigit(value[3]);

    private static string GetGameVersion(string gameRoot)
    {
        var executablePath = Path.Combine(gameRoot, "Game", "Bin", "TS4_x64.exe");
        if (!File.Exists(executablePath))
            return "unknown";

        var versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
        return versionInfo.ProductVersion ?? versionInfo.FileVersion ?? "unknown";
    }

    private static string ComputeFingerprint(
        string gameRoot,
        string gameVersion,
        IReadOnlyList<PackageFile> packages)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashPart(hash, gameRoot);
        AppendHashPart(hash, gameVersion);
        foreach (var package in packages)
        {
            AppendHashPart(hash, package.RelativePath);
            AppendHashPart(hash, package.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendHashPart(hash, package.LastWriteUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendHashPart(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> lengthPrefix = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, bytes.Length);
        hash.AppendData(lengthPrefix);
        hash.AppendData(bytes);
    }

    private static async Task<IndexReadResult> ReadIndexAsync(
        string path,
        string? expectedTgi,
        CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, System.IO.Compression.CompressionMode.Decompress, leaveOpen: false);
        using var reader = new StreamReader(gzip, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        IndexHeader? header = null;
        long resourceCount = 0;
        var matches = new List<IndexEntry>();
        string? line;
        var headerSeen = false;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                throw new InvalidDataException("Game index contains an empty record.");

            using var document = JsonDocument.Parse(line);
            var recordType = document.RootElement.GetProperty("recordType").GetString();
            if (recordType == "header")
            {
                if (headerSeen)
                    throw new InvalidDataException("Game index contains multiple headers.");
                header = document.RootElement.Deserialize<IndexHeader>(JsonOptions)
                    ?? throw new InvalidDataException("Game index header is empty.");
                headerSeen = true;
                continue;
            }

            if (recordType != "resource" || headerSeen)
                throw new InvalidDataException("Game index record order or type is invalid.");

            resourceCount++;
            var entry = document.RootElement.Deserialize<IndexEntry>(JsonOptions)
                ?? throw new InvalidDataException("Game index resource record is empty.");
            if (expectedTgi is not null &&
                string.Equals(entry.Tgi, expectedTgi, StringComparison.OrdinalIgnoreCase))
                matches.Add(entry);
        }

        if (header is null || header.Format != 2 || header.ResourceCount != resourceCount)
            throw new InvalidDataException("Game index header is missing or does not match its resource records.");

        return new IndexReadResult(header, resourceCount, matches);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private sealed record PackageFile(
        string FullPath,
        string RelativePath,
        string Layer,
        string Pack,
        int LayerPriority,
        long SizeBytes,
        DateTime LastWriteUtc)
    {
        public int ResourceCount { get; set; }
    }

    private sealed record IndexReadResult(
        IndexHeader Header,
        long ResourceCount,
        IReadOnlyList<IndexEntry> Matches);

    private sealed class IndexHeader
    {
        public required string RecordType { get; init; }
        public required int Format { get; init; }
        public required string GameRoot { get; init; }
        public required string GameVersion { get; init; }
        public required string Fingerprint { get; init; }
        public required int PackageCount { get; init; }
        public required long ResourceCount { get; init; }
        public required DateTimeOffset GeneratedUtc { get; init; }
        public required bool PrecedenceVerified { get; init; }
    }

    private sealed class IndexEntry
    {
        public required string RecordType { get; init; }
        public required string Tgi { get; init; }
        public required string Source { get; init; }
        public required string Layer { get; init; }
        public required string Pack { get; init; }
        public required int LayerPriority { get; init; }
        public required long PackageSizeBytes { get; init; }
        public required DateTime PackageModifiedUtc { get; init; }
    }
}
