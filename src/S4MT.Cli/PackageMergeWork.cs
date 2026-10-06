using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlamaLogic.Packages;

namespace S4MT.Cli;

internal sealed class PackageMergeWork
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<PackageMergeReport> MergeAsync(
        string manifestPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        PathGuard.EnsureDoesNotOverwrite(fullOutputPath, (fullManifestPath, "merge manifest"));

        var manifest = await ReadManifestAsync(fullManifestPath, cancellationToken);
        var inputPaths = ResolveInputs(fullManifestPath, fullOutputPath, manifest.Packages);
        var policy = ParseConflictPolicy(manifest.OnConflict);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var expected = new Dictionary<ResourceKey, string>();
            var providerByKey = new Dictionary<ResourceKey, string>();
            var conflicts = new List<PackageMergeConflict>();
            await using (var merged = new DataBasePackedFile())
            {
                foreach (var inputPath in inputPaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await using var source = await DataBasePackedFile.FromPathAsync(inputPath);
                    var keys = await source.GetKeysAsync(cancellationToken: cancellationToken);
                    foreach (var key in keys)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (expected.ContainsKey(key))
                        {
                            if (policy == ConflictPolicy.Error)
                                throw new CliInputException(
                                    $"Package merge found duplicate resource {ToResourceId(key)} in '{providerByKey[key]}' and '{inputPath}'. Set onConflict to 'first' or 'last' to choose explicitly.");

                            var keptPath = policy == ConflictPolicy.First ? providerByKey[key] : inputPath;
                            conflicts.Add(new PackageMergeConflict(
                                ToResourceId(key).ToString(),
                                providerByKey[key],
                                inputPath,
                                keptPath));
                            if (policy == ConflictPolicy.First)
                                continue;
                        }

                        var payload = await source.GetAsync(key, force: false, cancellationToken);
                        await merged.SetAsync(key, payload, CompressionMode.Auto, cancellationToken);
                        expected[key] = ComputeSha256(payload.Span);
                        providerByKey[key] = inputPath;
                    }
                }

                await merged.SaveAsAsync(temporaryPath);
            }

            await VerifyMergedPackageAsync(temporaryPath, expected, cancellationToken);
            var digest = await ComputeSha256Async(temporaryPath, cancellationToken);
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            return new PackageMergeReport(
                fullOutputPath,
                inputPaths,
                expected.Count,
                policy.ToString().ToLowerInvariant(),
                conflicts,
                digest,
                Verified: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static async Task<PackageMergeManifest> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        PackageMergeManifest? manifest;
        try
        {
            await using var input = File.OpenRead(path);
            manifest = await JsonSerializer.DeserializeAsync<PackageMergeManifest>(input, JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new CliInputException($"Invalid package merge manifest: {exception.Message}");
        }

        if (manifest is null)
            throw new CliInputException("Package merge manifest must contain a JSON object.");
        if (manifest.Format != 1)
            throw new CliInputException($"Unsupported package merge format '{manifest.Format}'; expected 1.");
        if (manifest.Packages is null || manifest.Packages.Count == 0)
            throw new CliInputException("Package merge manifest must list at least one package.");
        return manifest;
    }

    private static string[] ResolveInputs(string manifestPath, string outputPath, IReadOnlyList<string> declarations)
    {
        var root = Path.GetDirectoryName(manifestPath)
            ?? throw new CliInputException("Could not determine the package merge manifest directory.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new string[declarations.Count];
        for (var index = 0; index < declarations.Count; index++)
        {
            var declaration = declarations[index];
            if (string.IsNullOrWhiteSpace(declaration))
                throw new CliInputException("Package merge paths cannot be empty.");
            var fullPath = Path.IsPathFullyQualified(declaration)
                ? Path.GetFullPath(declaration)
                : Path.GetFullPath(Path.Combine(root, declaration));
            PathGuard.EnsureDoesNotOverwrite(outputPath, (fullPath, "input package"));
            if (!seen.Add(fullPath))
                throw new CliInputException($"Package merge manifest lists an input more than once: {fullPath}");
            if (!File.Exists(fullPath))
                throw new CliInputException($"Package merge input does not exist: {fullPath}");
            paths[index] = fullPath;
        }

        return paths;
    }

    private static ConflictPolicy ParseConflictPolicy(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "error" => ConflictPolicy.Error,
        "first" => ConflictPolicy.First,
        "last" => ConflictPolicy.Last,
        _ => throw new CliInputException("Package merge onConflict must be 'error', 'first', or 'last'.")
    };

    private static async Task VerifyMergedPackageAsync(
        string packagePath,
        IReadOnlyDictionary<ResourceKey, string> expected,
        CancellationToken cancellationToken)
    {
        await using var candidate = await DataBasePackedFile.FromPathAsync(packagePath);
        var keys = await candidate.GetKeysAsync(cancellationToken: cancellationToken);
        if (keys.Count != expected.Count || !keys.ToHashSet().SetEquals(expected.Keys))
            throw new InvalidDataException("Package merge read-back resource keys did not match the selected inputs.");

        foreach (var (key, expectedDigest) in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = await candidate.GetAsync(key, force: false, cancellationToken);
            if (!string.Equals(ComputeSha256(payload.Span), expectedDigest, StringComparison.Ordinal))
                throw new InvalidDataException($"Package merge read-back payload did not match resource {ToResourceId(key)}.");
        }
    }

    private static ResourceId ToResourceId(ResourceKey key) =>
        new(unchecked((uint)key.Type), key.Group, key.FullInstance);

    private static string ComputeSha256(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private enum ConflictPolicy
    {
        Error,
        First,
        Last
    }

    private sealed class PackageMergeManifest
    {
        public required int Format { get; init; }
        public required List<string> Packages { get; init; }
        public string? OnConflict { get; init; }
    }
}
