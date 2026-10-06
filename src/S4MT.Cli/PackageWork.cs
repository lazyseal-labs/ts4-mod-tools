using System.Security.Cryptography;
using System.Text;
using LlamaLogic.Packages;

namespace S4MT.Cli;

internal sealed class PackageWork
{
    public async Task<BuildReport> BuildAsync(
        BuildSpec spec,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);

        var outputDirectory = Path.GetDirectoryName(fullOutputPath)
            ?? throw new CliInputException("Could not determine the output directory.");
        Directory.CreateDirectory(outputDirectory);

        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var package = new DataBasePackedFile())
            {
                foreach (var resource in spec.Resources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = ToPackageKey(resource.Id);
                    await package.SetAsync(key, resource.Content, CompressionMode.Auto, cancellationToken);
                }

                await package.SaveAsAsync(temporaryPath);
            }

            var verification = await VerifyAsync(temporaryPath, spec, cancellationToken);
            if (!verification.Valid)
                throw new InvalidDataException(
                    $"Package read-back did not match its inputs. Missing: {string.Join(", ", verification.MissingKeys)}. " +
                    $"Extra: {string.Join(", ", verification.ExtraKeys)}. " +
                    $"Payload mismatch: {string.Join(", ", verification.PayloadMismatchKeys)}.");

            var digest = await ComputeSha256Async(temporaryPath, cancellationToken);
            File.Move(temporaryPath, fullOutputPath, overwrite: true);

            return new BuildReport(fullOutputPath, spec.Resources.Count, Verified: true, digest);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<PackageReport> InspectAsync(string packagePath, CancellationToken cancellationToken)
    {
        var fullPackagePath = Path.GetFullPath(packagePath);
        await using var package = await DataBasePackedFile.FromPathAsync(fullPackagePath);
        var sizes = await package.GetAllSizesAsync(null, cancellationToken);
        var resources = new List<ResourceInfo>(sizes.Count);

        foreach (var (key, sizeBytes) in sizes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resources.Add(new ResourceInfo(
                new ResourceId(unchecked((uint)key.Type), key.Group, key.FullInstance),
                sizeBytes));
        }

        resources.Sort((left, right) => string.CompareOrdinal(left.Id.ToString(), right.Id.ToString()));
        return new PackageReport(fullPackagePath, resources);
    }

    public async Task<PackageVerification> VerifyAsync(
        string packagePath,
        BuildSpec spec,
        CancellationToken cancellationToken)
    {
        var fullPackagePath = Path.GetFullPath(packagePath);
        await using var package = await DataBasePackedFile.FromPathAsync(fullPackagePath);
        var actualKeys = await package.GetKeysAsync(cancellationToken: cancellationToken);
        var actualKeySet = actualKeys.ToHashSet();
        var expected = spec.Resources.ToDictionary(
            resource => ToPackageKey(resource.Id),
            resource => resource);

        var missingKeys = expected.Keys
            .Where(key => !actualKeySet.Contains(key))
            .Select(key => ToResourceId(key).ToString())
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        var extraKeys = actualKeys
            .Where(key => !expected.ContainsKey(key))
            .Select(key => ToResourceId(key).ToString())
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        var payloadMismatchKeys = new List<string>();

        foreach (var (key, resource) in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!actualKeySet.Contains(key))
                continue;

            var payload = await package.GetAsync(key, force: false, cancellationToken);
            if (!payload.Span.SequenceEqual(resource.Content.Span))
                payloadMismatchKeys.Add(resource.Id.ToString());
        }

        payloadMismatchKeys.Sort(StringComparer.Ordinal);
        var isValid = missingKeys.Length == 0 && extraKeys.Length == 0 && payloadMismatchKeys.Count == 0;
        return new PackageVerification(
            fullPackagePath,
            expected.Count,
            actualKeys.Count,
            missingKeys,
            extraKeys,
            payloadMismatchKeys,
            isValid);
    }

    private static ResourceKey ToPackageKey(ResourceId id) =>
        new(unchecked((ResourceType)id.Type), id.Group, id.Instance);

    private static ResourceId ToResourceId(ResourceKey key) =>
        new(unchecked((uint)key.Type), key.Group, key.FullInstance);

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }
}
