using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;

namespace S4MT.Mcp;

internal sealed record GameIndexArtifact(
    string Sha256,
    string GameVersion,
    string Fingerprint,
    int PackageCount,
    long ResourceCount,
    DateTimeOffset GeneratedUtc,
    DateTimeOffset ActivatedUtc,
    string Path);

internal sealed record GameIndexStatus(
    string Status,
    string? Sha256,
    string? GameVersion,
    string? Fingerprint,
    int? PackageCount,
    long? ResourceCount,
    DateTimeOffset? GeneratedUtc,
    DateTimeOffset? ActivatedUtc);

internal sealed class GameIndexStore : IDisposable
{
    private const int MaximumJsonlLineBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string root;
    private readonly string stagingRoot;
    private readonly string blobsRoot;
    private readonly string activeManifestPath;
    private readonly long maximumBytes;
    private readonly long maximumUncompressedBytes;
    private readonly long maximumRetainedBytes;
    private readonly SemaphoreSlim importGate = new(1, 1);
    private GameIndexArtifact? active;

    internal GameIndexStore(HostOptions options)
    {
        root = HostOptions.Absolute(options.GameIndexCacheRoot);
        stagingRoot = Path.Combine(root, "staging");
        blobsRoot = Path.Combine(root, "blobs");
        activeManifestPath = Path.Combine(root, "active.json");
        maximumBytes = options.MaximumGameIndexBytes;
        maximumUncompressedBytes = options.MaximumGameIndexUncompressedBytes;
        maximumRetainedBytes = options.MaximumRetainedGameIndexBytes;
        Directory.CreateDirectory(stagingRoot);
        Directory.CreateDirectory(blobsRoot);
    }

    internal GameIndexArtifact? Active => Volatile.Read(ref active);

    internal GameIndexStatus GetStatus()
    {
        var current = Active;
        return current is null
            ? new GameIndexStatus("empty", null, null, null, null, null, null, null)
            : new GameIndexStatus("active", current.Sha256, current.GameVersion, current.Fingerprint,
                current.PackageCount, current.ResourceCount, current.GeneratedUtc, current.ActivatedUtc);
    }

    internal async Task InitializeAsync(CliProcessRunner runner, CancellationToken cancellationToken)
    {
        foreach (var stale in Directory.EnumerateFiles(stagingRoot, "*.upload", SearchOption.TopDirectoryOnly))
            File.Delete(stale);
        if (!File.Exists(activeManifestPath)) return;

        ActiveManifest manifest;
        try
        {
            await using var file = File.OpenRead(activeManifestPath);
            manifest = await JsonSerializer.DeserializeAsync<ActiveManifest>(file, JsonOptions, cancellationToken)
                ?? throw new JsonException("Active game-index manifest is empty.");
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new McpFailure("cache_unavailable", $"Could not read the active game-index manifest: {exception.Message}");
        }

        ValidateManifest(manifest);
        var blobPath = BlobPath(manifest.Sha256);
        if (!File.Exists(blobPath))
            throw new McpFailure("cache_unavailable", "The active game-index blob is missing.");
        if (new FileInfo(blobPath).Length > maximumBytes)
            throw new McpFailure("cache_unavailable", "The active game-index blob exceeds the configured compressed size limit.");
        try
        {
            await EnsureDecompressedBoundsAsync(blobPath, cancellationToken);
        }
        catch (IOException exception)
        {
            throw new McpFailure("cache_unavailable", $"The active game-index blob is not a valid bounded gzip stream: {exception.Message}");
        }
        var actualHash = await HashFileAsync(blobPath, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHash), Convert.FromHexString(manifest.Sha256)))
            throw new McpFailure("cache_unavailable", "The active game-index blob failed its SHA-256 integrity check.");

        var validated = await ValidateWithCliAsync(blobPath, runner, cancellationToken);
        if (!Matches(manifest, validated))
            throw new McpFailure("cache_unavailable", "The active game-index metadata does not match the validated blob.");
        Volatile.Write(ref active, ToArtifact(manifest, blobPath));
    }

    internal async Task<GameIndexArtifact> ImportAsync(
        Stream upload,
        long? contentLength,
        CliProcessRunner runner,
        CancellationToken cancellationToken)
    {
        if (contentLength is > 0 && contentLength > maximumBytes)
            throw new McpFailure("game_index_too_large", "The uploaded game index exceeds the configured size limit.");

        cancellationToken.ThrowIfCancellationRequested();
        if (!await importGate.WaitAsync(0, cancellationToken))
            throw new McpFailure("cache_import_busy", "Another game-index import is already in progress.");
        var stagingPath = Path.Combine(stagingRoot, $"{Guid.NewGuid():N}.upload");
        try
        {
            string sha256;
            long bytes = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var count = await upload.ReadAsync(buffer, cancellationToken);
                    if (count == 0) break;
                    bytes = checked(bytes + count);
                    if (bytes > maximumBytes)
                        throw new McpFailure("game_index_too_large", "The uploaded game index exceeds the configured size limit.");
                    hash.AppendData(buffer.AsSpan(0, count));
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                if (bytes == 0)
                    throw new McpFailure("invalid_game_index", "The uploaded game index is empty.");
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
                sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            await EnsureDecompressedBoundsAsync(stagingPath, cancellationToken);
            var indexInfo = await ValidateWithCliAsync(stagingPath, runner, cancellationToken);
            var blobPath = BlobPath(sha256);
            if (File.Exists(blobPath))
            {
                var existingHash = await HashFileAsync(blobPath, cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(existingHash), Convert.FromHexString(sha256)))
                    throw new McpFailure("cache_unavailable", "A content-addressed game-index blob has an invalid digest.");
                File.Delete(stagingPath);
            }
            else
            {
                EnsureRetainedCapacity(bytes);
                File.Move(stagingPath, blobPath);
            }

            var manifest = new ActiveManifest(
                Format: 1,
                Sha256: sha256,
                GameVersion: indexInfo.GameVersion,
                Fingerprint: indexInfo.Fingerprint,
                PackageCount: indexInfo.PackageCount,
                ResourceCount: indexInfo.ResourceCount,
                GeneratedUtc: indexInfo.GeneratedUtc,
                ActivatedUtc: DateTimeOffset.UtcNow);
            await WriteManifestAsync(manifest, cancellationToken);
            var artifact = ToArtifact(manifest, blobPath);
            Volatile.Write(ref active, artifact);
            return artifact;
        }
        catch (McpFailure)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            throw new McpFailure("invalid_game_index", $"Could not import the game index: {exception.Message}");
        }
        finally
        {
            try { if (File.Exists(stagingPath)) File.Delete(stagingPath); }
            finally { importGate.Release(); }
        }
    }

    private async Task<ValidatedIndex> ValidateWithCliAsync(
        string path,
        CliProcessRunner runner,
        CancellationToken cancellationToken)
    {
        CliReport report;
        try
        {
            report = await runner.RunAsync(
                ["game", "find", "--index", path, "--type", "0x00000000", "--limit", "1"],
                "game.find",
                cancellationToken);
        }
        catch (McpFailure exception)
        {
            throw new McpFailure("invalid_game_index", $"The CLI rejected the uploaded game index: {exception.Message}", exception.ExitCode, exception.Stderr);
        }

        var value = report.Report;
        return new ValidatedIndex(
            value.GetProperty("gameVersion").GetString()!,
            value.GetProperty("fingerprint").GetString()!,
            value.GetProperty("packageCount").GetInt32(),
            value.GetProperty("resourceCount").GetInt64(),
            value.GetProperty("generatedUtc").GetDateTimeOffset());
    }

    private async Task EnsureDecompressedBoundsAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
        var buffer = new byte[64 * 1024];
        long totalBytes = 0;
        var lineBytes = 0;
        while (true)
        {
            var count = await gzip.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (count == 0) break;
            if (totalBytes > maximumUncompressedBytes - count)
                throw new McpFailure("game_index_too_large", "The decompressed game index exceeds the configured size limit.");
            totalBytes += count;
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] == (byte)'\n')
                {
                    lineBytes = 0;
                    continue;
                }
                if (++lineBytes > MaximumJsonlLineBytes)
                    throw new McpFailure("invalid_game_index", "A game-index JSONL record exceeds the 1 MiB line limit.");
            }
        }
        if (totalBytes == 0)
            throw new McpFailure("invalid_game_index", "The uploaded game index does not contain any decompressed data.");
    }

    private void EnsureRetainedCapacity(long newBlobBytes)
    {
        long retainedBytes = 0;
        foreach (var blob in Directory.EnumerateFiles(blobsRoot, "*.jsonl.gz", SearchOption.TopDirectoryOnly))
        {
            var length = new FileInfo(blob).Length;
            if (retainedBytes > maximumRetainedBytes - length)
                throw new McpFailure("cache_storage_limit", "Retained game-index blobs already exceed maximumRetainedGameIndexBytes. Stop the service and remove unneeded old blobs before importing another version.");
            retainedBytes += length;
        }
        if (newBlobBytes > maximumRetainedBytes - retainedBytes)
            throw new McpFailure("cache_storage_limit", "Importing this version would exceed maximumRetainedGameIndexBytes. Stop the service and remove unneeded old blobs before importing it.");
    }

    private async Task WriteManifestAsync(ActiveManifest manifest, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(root, $".active-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, manifest, JsonOptions, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, activeManifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string BlobPath(string sha256) => Path.Combine(blobsRoot, $"{sha256}.jsonl.gz");

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(file, cancellationToken);
        return Convert.ToHexStringLower(digest);
    }

    private static GameIndexArtifact ToArtifact(ActiveManifest manifest, string path) => new(
        manifest.Sha256, manifest.GameVersion, manifest.Fingerprint, manifest.PackageCount, manifest.ResourceCount,
        manifest.GeneratedUtc, manifest.ActivatedUtc, path);

    private static bool Matches(ActiveManifest manifest, ValidatedIndex validated) =>
        string.Equals(manifest.GameVersion, validated.GameVersion, StringComparison.Ordinal) &&
        string.Equals(manifest.Fingerprint, validated.Fingerprint, StringComparison.Ordinal) &&
        manifest.PackageCount == validated.PackageCount && manifest.ResourceCount == validated.ResourceCount &&
        manifest.GeneratedUtc == validated.GeneratedUtc;

    private static void ValidateManifest(ActiveManifest manifest)
    {
        if (manifest.Format != 1 || string.IsNullOrWhiteSpace(manifest.Sha256) || manifest.Sha256.Length != 64 ||
            !manifest.Sha256.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(manifest.GameVersion) ||
            string.IsNullOrWhiteSpace(manifest.Fingerprint) || manifest.PackageCount < 0 || manifest.ResourceCount < 0 ||
            manifest.GeneratedUtc == default || manifest.ActivatedUtc == default)
            throw new McpFailure("cache_unavailable", "The active game-index manifest has invalid metadata.");
    }

    public void Dispose() => importGate.Dispose();

    private sealed record ActiveManifest(
        int Format,
        string Sha256,
        string GameVersion,
        string Fingerprint,
        int PackageCount,
        long ResourceCount,
        DateTimeOffset GeneratedUtc,
        DateTimeOffset ActivatedUtc);

    private sealed record ValidatedIndex(
        string GameVersion,
        string Fingerprint,
        int PackageCount,
        long ResourceCount,
        DateTimeOffset GeneratedUtc);
}
