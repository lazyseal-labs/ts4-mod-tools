using System.Security.Cryptography;
using LlamaLogic.Packages;

namespace S4MT.Cli;

internal sealed class CaspWork
{
    private const uint CasPartType = 0x034AEECB;

    internal async Task<CaspInspectReport> InspectAsync(
        string packagePath,
        ResourceId resourceId,
        CancellationToken cancellationToken)
    {
        EnsureCasPart(resourceId);
        var fullPackagePath = Path.GetFullPath(packagePath);
        await using var package = await DataBasePackedFile.FromPathAsync(fullPackagePath);
        var key = ToPackageKey(resourceId);
        if (!await package.ContainsKeyAsync(key, cancellationToken))
            throw new CliInputException($"Package does not contain CASP resource {resourceId}.");

        var payload = await package.GetAsync(key, force: false, cancellationToken);
        var document = DecodeInput(resourceId, payload);
        return new CaspInspectReport(
            fullPackagePath,
            resourceId.ToString(),
            document.Version,
            document.Name,
            document.NameByteLength,
            document.PayloadLength);
    }

    internal async Task<CaspRenameReport> RenameAsync(
        string packagePath,
        ResourceId resourceId,
        string name,
        string outputPath,
        CancellationToken cancellationToken)
    {
        EnsureCasPart(resourceId);
        var fullPackagePath = Path.GetFullPath(packagePath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        PathGuard.EnsureDoesNotOverwrite(fullOutputPath, (fullPackagePath, "source package"));

        var outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = CreateTemporaryPath(fullOutputPath);
        try
        {
            int originalResourceCount;
            string originalName;
            uint version;
            int payloadLength;
            byte[] expectedPayload;

            await using (var source = await DataBasePackedFile.FromPathAsync(fullPackagePath))
            {
                var key = ToPackageKey(resourceId);
                if (!await source.ContainsKeyAsync(key, cancellationToken))
                    throw new CliInputException($"Source package does not contain CASP resource {resourceId}.");

                var originalKeys = (await source.GetKeysAsync(cancellationToken: cancellationToken)).ToHashSet();
                originalResourceCount = originalKeys.Count;
                var payload = await source.GetAsync(key, force: false, cancellationToken);
                var document = DecodeInput(resourceId, payload);
                originalName = document.Name;
                version = document.Version;
                payloadLength = document.PayloadLength;
                try
                {
                    expectedPayload = CaspCodec.Rename(document, name);
                }
                catch (InvalidDataException exception)
                {
                    throw new CliInputException(exception.Message);
                }

                source.Set(key, expectedPayload, CompressionMode.Auto);
                await source.SaveAsAsync(temporaryPath);

                await using var candidate = await DataBasePackedFile.FromPathAsync(temporaryPath);
                var candidateKeys = (await candidate.GetKeysAsync(cancellationToken: cancellationToken)).ToHashSet();
                if (!originalKeys.SetEquals(candidateKeys))
                    throw new InvalidDataException("CASP rename changed the package resource-key set.");

                foreach (var unchangedKey in originalKeys.Where(existingKey => existingKey != key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var before = await source.GetAsync(unchangedKey, force: false, cancellationToken);
                    var after = await candidate.GetAsync(unchangedKey, force: false, cancellationToken);
                    if (!before.Span.SequenceEqual(after.Span))
                        throw new InvalidDataException($"CASP rename changed unrelated resource {ToResourceId(unchangedKey)}.");
                }

                var verifiedPayload = await candidate.GetAsync(key, force: false, cancellationToken);
                if (!expectedPayload.AsSpan().SequenceEqual(verifiedPayload.Span))
                    throw new InvalidDataException($"CASP payload did not match after package read-back for {resourceId}.");

                var verifiedDocument = DecodeInput(resourceId, verifiedPayload);
                if (!string.Equals(verifiedDocument.Name, name, StringComparison.Ordinal))
                    throw new InvalidDataException($"CASP name did not match after package read-back for {resourceId}.");
            }

            var digest = await ComputeSha256Async(temporaryPath, cancellationToken);
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            return new CaspRenameReport(
                fullPackagePath,
                fullOutputPath,
                resourceId.ToString(),
                version,
                originalName,
                name,
                payloadLength,
                originalResourceCount,
                digest,
                Verified: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static CaspDocument DecodeInput(ResourceId resourceId, ReadOnlyMemory<byte> payload)
    {
        try
        {
            return CaspCodec.Decode(payload);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            throw new CliInputException($"Invalid CASP resource {resourceId}: {exception.Message}");
        }
    }

    private static void EnsureCasPart(ResourceId id)
    {
        if (id.Type != CasPartType)
            throw new CliInputException($"Resource {id} is not a CASP resource.");
    }

    private static ResourceKey ToPackageKey(ResourceId id) =>
        new(unchecked((ResourceType)id.Type), id.Group, id.Instance);

    private static ResourceId ToResourceId(ResourceKey key) =>
        new(unchecked((uint)key.Type), key.Group, key.FullInstance);

    private static string CreateTemporaryPath(string outputPath) =>
        Path.Combine(Path.GetDirectoryName(outputPath)!, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}

internal sealed record CaspInspectReport(
    string Package,
    string Tgi,
    uint Version,
    string Name,
    int NameByteLength,
    int PayloadBytes);

internal sealed record CaspRenameReport(
    string SourcePackage,
    string OutputPackage,
    string Tgi,
    uint Version,
    string PreviousName,
    string Name,
    int PayloadBytes,
    int PackageResourceCount,
    string Sha256,
    bool Verified);
