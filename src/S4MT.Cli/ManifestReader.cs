using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace S4MT.Cli;

internal static class ManifestReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<BuildSpec> ReadAsync(
        string manifestPath,
        CancellationToken cancellationToken,
        string? outputPath = null,
        bool allowEmptyResources = false,
        bool requireContainedSources = false)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        var fullOutputPath = outputPath is null ? null : Path.GetFullPath(outputPath);
        if (fullOutputPath is not null)
            PathGuard.EnsureDoesNotOverwrite(fullOutputPath, (fullManifestPath, "manifest"));

        var manifestText = await File.ReadAllTextAsync(fullManifestPath, Encoding.UTF8, cancellationToken);
        BuildManifest manifest;

        try
        {
            manifest = JsonSerializer.Deserialize<BuildManifest>(manifestText, JsonOptions)
                ?? throw new CliInputException("Manifest must contain a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new CliInputException($"Invalid manifest: {exception.Message}");
        }

        if (manifest.Format != 1)
            throw new CliInputException($"Unsupported manifest format {manifest.Format}; expected 1.");

        if (manifest.Resources is null || (!allowEmptyResources && manifest.Resources.Count == 0))
            throw new CliInputException("Manifest must contain at least one resource.");

        var root = Path.GetDirectoryName(fullManifestPath)
            ?? throw new CliInputException("Could not determine the manifest directory.");
        var resources = new List<EncodedResource>(manifest.Resources.Count);
        var seenIds = new HashSet<ResourceId>();

        foreach (var declaration in manifest.Resources)
        {
            if (declaration is null)
                throw new CliInputException("Manifest resources cannot contain null entries.");

            if (string.IsNullOrWhiteSpace(declaration.Source))
                throw new CliInputException("Each resource must define a source path.");
            if (requireContainedSources && Path.IsPathFullyQualified(declaration.Source))
                throw new CliInputException("Project resource sources must use relative paths.");

            var id = ResourceIdParser.Parse(declaration.Type, declaration.Group, declaration.Instance);

            if (!seenIds.Add(id))
                throw new CliInputException($"Duplicate resource key {id}.");

            var sourcePath = Path.IsPathFullyQualified(declaration.Source)
                ? Path.GetFullPath(declaration.Source)
                : Path.GetFullPath(Path.Combine(root, declaration.Source));

            if (requireContainedSources)
                PathGuard.EnsureWithinDirectory(sourcePath, root, "Project resource source");
            if (fullOutputPath is not null)
                PathGuard.EnsureDoesNotOverwrite(fullOutputPath, (sourcePath, "source file"));

            if (!File.Exists(sourcePath))
                throw new CliInputException($"Resource source does not exist: {sourcePath}");

            var encoding = ParseEncoding(declaration.Encoding);
            var payload = await ReadPayloadAsync(sourcePath, encoding, cancellationToken);
            resources.Add(new EncodedResource(id, payload, sourcePath, encoding));
        }

        return new BuildSpec(resources);
    }

    private static async Task<byte[]> ReadPayloadAsync(
        string sourcePath,
        ResourceEncoding encoding,
        CancellationToken cancellationToken)
    {
        var sourceBytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        if (encoding == ResourceEncoding.Bytes)
            return sourceBytes;

        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(sourceBytes);
            return Encoding.UTF8.GetBytes(text);
        }
        catch (DecoderFallbackException exception)
        {
            throw new CliInputException($"Resource source is not valid UTF-8: {sourcePath} ({exception.Message})");
        }
    }

    private static ResourceEncoding ParseEncoding(string? encodingName)
    {
        if (string.Equals(encodingName, "bytes", StringComparison.OrdinalIgnoreCase))
            return ResourceEncoding.Bytes;
        if (string.Equals(encodingName, "utf8", StringComparison.OrdinalIgnoreCase))
            return ResourceEncoding.Utf8;
        throw new CliInputException("Each resource must set encoding to either 'bytes' or 'utf8'.");
    }

    private sealed class BuildManifest
    {
        public required int Format { get; init; }
        public required List<ResourceDeclaration> Resources { get; init; }
    }

    private sealed class ResourceDeclaration
    {
        public required string Type { get; init; }
        public required string Group { get; init; }
        public required string Instance { get; init; }
        public required string Source { get; init; }
        public required string Encoding { get; init; }
    }
}
