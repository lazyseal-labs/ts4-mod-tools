using LlamaLogic.Packages;

namespace S4MT.Cli;

internal sealed class ConflictWork(GameCatalogWork gameCatalog)
{
    public async Task<GameConflictReport> CompareWithGameAsync(
        string packagePath,
        string indexPath,
        CancellationToken cancellationToken)
    {
        var fullPackagePath = Path.GetFullPath(packagePath);
        await using var package = await DataBasePackedFile.FromPathAsync(fullPackagePath);
        var keys = await package.GetKeysAsync(cancellationToken: cancellationToken);
        var packageTgis = keys
            .Select(ToResourceId)
            .Select(id => id.ToString())
            .OrderBy(tgi => tgi, StringComparer.Ordinal)
            .ToArray();
        var lookup = await gameCatalog.LookupProvidersAsync(
            indexPath,
            packageTgis.ToHashSet(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        var conflicts = packageTgis
            .Where(tgi => lookup.ProvidersByTgi.ContainsKey(tgi))
            .Select(tgi =>
            {
                var providers = lookup.ProvidersByTgi[tgi];
                return new GameConflictResource(
                    tgi,
                    providers.Count,
                    providers,
                    lookup.PrecedenceVerified && providers.Count == 1 ? providers[0] : null);
            })
            .ToArray();

        return new GameConflictReport(
            fullPackagePath,
            lookup.Index,
            packageTgis.Length,
            conflicts.Length,
            lookup.PrecedenceVerified,
            conflicts);
    }

    public async Task<PackageConflictReport> ComparePackagesAsync(
        string leftPackagePath,
        string rightPackagePath,
        CancellationToken cancellationToken)
    {
        var leftPath = Path.GetFullPath(leftPackagePath);
        var rightPath = Path.GetFullPath(rightPackagePath);
        var leftKeys = await ReadKeysAsync(leftPath, cancellationToken);
        var rightKeys = await ReadKeysAsync(rightPath, cancellationToken);
        var sharedTgis = leftKeys
            .Intersect(rightKeys)
            .Select(ToResourceId)
            .Select(id => id.ToString())
            .OrderBy(tgi => tgi, StringComparer.Ordinal)
            .ToArray();

        return new PackageConflictReport(leftPath, rightPath, leftKeys.Count, rightKeys.Count, sharedTgis);
    }

    private static async Task<HashSet<ResourceKey>> ReadKeysAsync(string path, CancellationToken cancellationToken)
    {
        await using var package = await DataBasePackedFile.FromPathAsync(path);
        return (await package.GetKeysAsync(cancellationToken: cancellationToken)).ToHashSet();
    }

    private static ResourceId ToResourceId(ResourceKey key) =>
        new(unchecked((uint)key.Type), key.Group, key.FullInstance);
}
