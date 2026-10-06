using System.Security.Cryptography;
using LlamaLogic.Packages;

namespace S4MT.Cli;

internal sealed class ResourceWork
{
    public async Task<ResourceExportReport> ExportAsync(
        string packagePath,
        ResourceId resourceId,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var fullPackagePath = Path.GetFullPath(packagePath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        PathGuard.EnsureDoesNotOverwrite(fullOutputPath, (fullPackagePath, "source package"));

        var outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = CreateTemporaryPath(fullOutputPath);
        try
        {
            ReadOnlyMemory<byte> payload;
            await using (var package = await DataBasePackedFile.FromPathAsync(fullPackagePath))
            {
                var key = ToPackageKey(resourceId);
                if (!await package.ContainsKeyAsync(key, cancellationToken))
                    throw new CliInputException($"Package does not contain resource {resourceId}.");
                payload = await package.GetAsync(key, force: false, cancellationToken);
            }

            await File.WriteAllBytesAsync(temporaryPath, payload.ToArray(), cancellationToken);
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            return new ResourceExportReport(
                fullPackagePath,
                fullOutputPath,
                resourceId.ToString(),
                payload.Length,
                ComputeSha256(payload.Span));
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<ResourceImportReport> ImportAsync(
        string packagePath,
        ResourceId resourceId,
        string payloadPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var fullPackagePath = Path.GetFullPath(packagePath);
        var fullPayloadPath = Path.GetFullPath(payloadPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        PathGuard.EnsureDoesNotOverwrite(
            fullOutputPath,
            (fullPackagePath, "source package"),
            (fullPayloadPath, "payload source"));

        if (!File.Exists(fullPayloadPath))
            throw new CliInputException($"Resource payload file does not exist: {fullPayloadPath}");
        var payload = await File.ReadAllBytesAsync(fullPayloadPath, cancellationToken);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = CreateTemporaryPath(fullOutputPath);

        try
        {
            int originalResourceCount;
            await using (var source = await DataBasePackedFile.FromPathAsync(fullPackagePath))
            {
                var key = ToPackageKey(resourceId);
                if (!await source.ContainsKeyAsync(key, cancellationToken))
                    throw new CliInputException($"Source package does not contain resource {resourceId}.");

                var originalKeys = (await source.GetKeysAsync(cancellationToken: cancellationToken)).ToHashSet();
                originalResourceCount = originalKeys.Count;
                source.Set(key, payload, CompressionMode.Auto);
                await source.SaveAsAsync(temporaryPath);

                await using var candidate = await DataBasePackedFile.FromPathAsync(temporaryPath);
                var candidateKeys = (await candidate.GetKeysAsync(cancellationToken: cancellationToken)).ToHashSet();
                if (!originalKeys.SetEquals(candidateKeys))
                    throw new InvalidDataException("Raw resource import changed the package resource-key set.");

                foreach (var unchangedKey in originalKeys.Where(existingKey => existingKey != key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var before = await source.GetAsync(unchangedKey, force: false, cancellationToken);
                    var after = await candidate.GetAsync(unchangedKey, force: false, cancellationToken);
                    if (!before.Span.SequenceEqual(after.Span))
                        throw new InvalidDataException($"Raw resource import changed unrelated resource {ToResourceId(unchangedKey)}.");
                }

                var verifiedPayload = await candidate.GetAsync(key, force: false, cancellationToken);
                if (!payload.AsSpan().SequenceEqual(verifiedPayload.Span))
                    throw new InvalidDataException($"Raw resource import payload did not match after package read-back for {resourceId}.");
            }

            var digest = await ComputeSha256Async(temporaryPath, cancellationToken);
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            return new ResourceImportReport(
                fullPackagePath,
                fullOutputPath,
                resourceId.ToString(),
                payload.Length,
                originalResourceCount,
                ComputeSha256(payload),
                digest,
                Verified: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static ResourceKey ToPackageKey(ResourceId id) =>
        new(unchecked((ResourceType)id.Type), id.Group, id.Instance);

    private static ResourceId ToResourceId(ResourceKey key) =>
        new(unchecked((uint)key.Type), key.Group, key.FullInstance);

    private static string CreateTemporaryPath(string outputPath) =>
        Path.Combine(Path.GetDirectoryName(outputPath)!, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");

    private static string ComputeSha256(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
