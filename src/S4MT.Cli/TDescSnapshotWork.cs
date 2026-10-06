using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace S4MT.Cli;

internal static class TDescSnapshotWork
{
    private const int SnapshotFormat = 2;
    private const int ParserFormat = 2;
    private const int MaximumFiles = 20_000;
    private const int MaximumFileBytes = 64 * 1024 * 1024;
    private const long MaximumTotalBytes = 512L * 1024 * 1024;
    private const int MaximumXmlDepth = 128;
    private static readonly HashSet<string> OmittedDisplayAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "description"
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        MaxDepth = MaximumXmlDepth + 16
    };

    internal static async Task<TDescSnapshotReport> RefreshAsync(
        string sourceDirectory,
        string gameVersion,
        string source,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        var outputRoot = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(sourceRoot))
            throw new CliInputException($"TDesc source directory does not exist: {sourceRoot}");
        if (!IsGameVersion(gameVersion))
            throw new CliInputException("Game version must use four numeric components, for example 1.128.90.1030.");
        if (string.IsNullOrWhiteSpace(source) || source.Length > 256)
            throw new CliInputException("TDesc source label must contain 1 to 256 characters.");

        PathGuard.EnsureOutsideDirectory(outputRoot, sourceRoot, "TDesc source directory");
        PathGuard.EnsureDoesNotOverwrite(outputRoot, (sourceRoot, "TDesc source directory"));

        var inputFiles = Directory.EnumerateFiles(sourceRoot, "*.tdesc", SearchOption.AllDirectories)
            .Select(path => new
            {
                FullPath = Path.GetFullPath(path),
                RelativePath = Path.GetRelativePath(sourceRoot, path).Replace('\\', '/')
            })
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        if (inputFiles.Length == 0)
            throw new CliInputException($"No .tdesc files were found in {sourceRoot}.");
        if (inputFiles.Length > MaximumFiles)
            throw new CliInputException($"TDesc source contains more than {MaximumFiles} files.");

        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (inputFiles.Select(file => file.RelativePath).Distinct(pathComparer).Count() != inputFiles.Length)
            throw new CliInputException("TDesc source contains duplicate paths differing only by case.");

        var definitions = new List<TDescClassIndex>();
        var sourceFiles = new List<TDescSourceFile>(inputFiles.Length);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        var temporaryRoot = Path.Combine(
            Path.GetDirectoryName(outputRoot)!,
            $".{Path.GetFileName(outputRoot)}.{Guid.NewGuid():N}.tmp");
        var definitionDirectory = Path.Combine(temporaryRoot, "definitions");
        Directory.CreateDirectory(definitionDirectory);

        try
        {
            foreach (var inputFile in inputFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PathGuard.EnsureWithinDirectory(inputFile.FullPath, sourceRoot, "TDesc source file");
                var fileInfo = new FileInfo(inputFile.FullPath);
                if (fileInfo.Length > MaximumFileBytes)
                    throw new CliInputException($"TDesc file exceeds the {MaximumFileBytes} byte limit: {inputFile.RelativePath}");
                totalBytes = checked(totalBytes + fileInfo.Length);
                if (totalBytes > MaximumTotalBytes)
                    throw new CliInputException($"TDesc source exceeds the {MaximumTotalBytes} byte aggregate limit.");

                var bytes = await File.ReadAllBytesAsync(inputFile.FullPath, cancellationToken);
                if (bytes.LongLength != fileInfo.Length)
                    throw new CliInputException($"TDesc source changed while being read: {inputFile.RelativePath}");
                var digest = ComputeSha256(bytes);
                sourceFiles.Add(new TDescSourceFile(inputFile.RelativePath, bytes.Length, digest));
                using var stream = new MemoryStream(bytes, writable: false);
                using var reader = CreateXmlReader(stream, MaximumFileBytes);
                await ReadSourceFileAsync(reader, inputFile.RelativePath, temporaryRoot,
                    definitionDirectory, identities, definitions, cancellationToken);
            }

            var orderedDefinitions = definitions
                .OrderBy(item => item.DefinitionKind, StringComparer.Ordinal)
                .ThenBy(item => item.InstanceType, StringComparer.Ordinal)
                .ThenBy(item => item.Module, StringComparer.Ordinal)
                .ThenBy(item => item.Class, StringComparer.Ordinal)
                .ThenBy(item => item.SourceFile, StringComparer.Ordinal)
                .ToArray();
            var orderedSourceFiles = sourceFiles.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
            var index = new TDescSnapshotIndex(SnapshotFormat, orderedDefinitions);
            var indexBytes = JsonSerializer.SerializeToUtf8Bytes(index, JsonOptions);
            var indexSha256 = ComputeSha256(indexBytes);
            var snapshotSha256 = ComputeSnapshotDigest(gameVersion, ParserFormat, orderedSourceFiles, indexBytes);
            var manifest = new TDescSnapshotManifest(
                SnapshotFormat,
                ParserFormat,
                gameVersion,
                source,
                DateTimeOffset.UtcNow,
                orderedSourceFiles,
                indexSha256,
                snapshotSha256);

            await File.WriteAllBytesAsync(Path.Combine(temporaryRoot, "index.json"), indexBytes, cancellationToken);
            await WriteJsonAsync(Path.Combine(temporaryRoot, "manifest.json"), manifest, cancellationToken);

            if (File.Exists(outputRoot))
                throw new CliInputException($"TDesc snapshot output is a file: {outputRoot}");
            if (Directory.Exists(outputRoot))
            {
                var existing = await LoadAsync(outputRoot, cancellationToken);
                await VerifyDefinitionsAsync(existing, cancellationToken);
                if (!string.Equals(existing.Manifest.SnapshotSha256, snapshotSha256, StringComparison.Ordinal))
                    throw new CliInputException($"TDesc snapshot output already contains different data: {outputRoot}");
                return ToReport(outputRoot, existing.Manifest, existing.Index, reused: true);
            }

            Directory.Move(temporaryRoot, outputRoot);
            return ToReport(outputRoot, manifest, index, reused: false);
        }
        finally
        {
            DeleteOwnedTemporaryDirectory(temporaryRoot, outputRoot);
        }
    }

    internal static async Task<TDescSnapshotData> LoadAsync(
        string snapshotDirectory,
        CancellationToken cancellationToken)
    {
        var fullDirectory = Path.GetFullPath(snapshotDirectory);
        var manifestPath = Path.Combine(fullDirectory, "manifest.json");
        var indexPath = Path.Combine(fullDirectory, "index.json");
        if (!Directory.Exists(fullDirectory) || !File.Exists(manifestPath) || !File.Exists(indexPath))
            throw new CliInputException($"TDesc snapshot is missing manifest.json or index.json: {fullDirectory}");

        TDescSnapshotManifest manifest;
        TDescSnapshotIndex index;
        byte[] indexBytes;
        try
        {
            await using var manifestStream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer.DeserializeAsync<TDescSnapshotManifest>(
                manifestStream, JsonOptions, cancellationToken)
                ?? throw new JsonException("Snapshot manifest is empty.");
            indexBytes = await File.ReadAllBytesAsync(indexPath, cancellationToken);
            index = JsonSerializer.Deserialize<TDescSnapshotIndex>(indexBytes, JsonOptions)
                ?? throw new JsonException("Snapshot index is empty.");
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            throw new CliInputException($"Could not read TDesc snapshot: {exception.Message}");
        }

        if (manifest.Format != SnapshotFormat || manifest.ParserFormat != ParserFormat || index.Format != SnapshotFormat)
            throw new CliInputException("TDesc snapshot format or parser version is unsupported; refresh the source archive.");
        if (!IsGameVersion(manifest.GameVersion) || string.IsNullOrWhiteSpace(manifest.Source) ||
            manifest.Files is null || index.Classes is null)
            throw new CliInputException("TDesc snapshot manifest or index is incomplete.");
        if (!IsSha256(manifest.IndexSha256) || !IsSha256(manifest.SnapshotSha256))
            throw new CliInputException("TDesc snapshot manifest contains an invalid digest.");

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in index.Classes)
        {
            if (item.DefinitionKind is not ("Instance" or "Module"))
                throw new CliInputException($"TDesc snapshot contains an unknown definition kind: {item.DefinitionKind}");
            if (!IsSha256(item.Sha256) || item.Bytes <= 0 || item.Fields is null || string.IsNullOrWhiteSpace(item.SourceFile))
                throw new CliInputException("TDesc snapshot index contains an incomplete definition reference.");
            if (item.SourceFile.StartsWith('/') || item.SourceFile.Contains(':') ||
                item.SourceFile.Split('/').Any(part => part is ".." or ""))
                throw new CliInputException($"TDesc snapshot source path is not relative: {item.SourceFile}");
            if (item.DefinitionKind == "Instance" &&
                (string.IsNullOrWhiteSpace(item.InstanceType) || string.IsNullOrWhiteSpace(item.Class) || string.IsNullOrWhiteSpace(item.Module)))
                throw new CliInputException("TDesc Instance definition is missing its instance type, class, or module.");
            if (item.DefinitionKind == "Module" && string.IsNullOrWhiteSpace(item.Module))
                throw new CliInputException("TDesc Module definition is missing its module name.");
            if (!identities.Add(GetIdentity(item.DefinitionKind, item.InstanceType, item.Class, item.Module)))
                throw new CliInputException($"TDesc snapshot contains duplicate identity: {GetIdentity(item.DefinitionKind, item.InstanceType, item.Class, item.Module)}");
        }

        var indexDigest = ComputeSha256(indexBytes);
        if (!string.Equals(indexDigest, manifest.IndexSha256, StringComparison.OrdinalIgnoreCase))
            throw new CliInputException("TDesc snapshot index digest does not match the manifest.");
        var snapshotDigest = ComputeSnapshotDigest(
            manifest.GameVersion,
            manifest.ParserFormat,
            manifest.Files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(),
            indexBytes);
        if (!string.Equals(snapshotDigest, manifest.SnapshotSha256, StringComparison.OrdinalIgnoreCase))
            throw new CliInputException("TDesc snapshot digest does not match the manifest.");

        return new TDescSnapshotData(fullDirectory, manifest, index);
    }

    internal static async Task<TDescDefinition> LoadDefinitionAsync(
        TDescSnapshotData snapshot,
        TDescClassIndex entry,
        CancellationToken cancellationToken)
    {
        EnsureDefinitionEntry(snapshot, entry);
        var path = GetDefinitionPath(snapshot.Directory, entry.Sha256);
        PathGuard.EnsureWithinDirectory(path, snapshot.Directory, "TDesc definition shard");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != entry.Bytes)
            throw new CliInputException($"TDesc definition shard is missing or has changed length: {entry.SourceFile}");

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (!string.Equals(ComputeSha256(bytes), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CliInputException($"TDesc definition shard digest does not match the snapshot index: {entry.SourceFile}");

        TDescDefinition definition;
        try
        {
            definition = JsonSerializer.Deserialize<TDescDefinition>(bytes, JsonOptions)
                ?? throw new JsonException("Definition shard is empty.");
        }
        catch (JsonException exception)
        {
            throw new CliInputException($"Could not read TDesc definition shard '{entry.SourceFile}': {exception.Message}");
        }

        if (!string.Equals(definition.DefinitionKind, entry.DefinitionKind, StringComparison.Ordinal) ||
            !string.Equals(definition.InstanceType, entry.InstanceType, StringComparison.Ordinal) ||
            !string.Equals(definition.Class, entry.Class, StringComparison.Ordinal) ||
            !string.Equals(definition.Module, entry.Module, StringComparison.Ordinal) ||
            !string.Equals(definition.SourceFile, entry.SourceFile, StringComparison.Ordinal))
            throw new CliInputException($"TDesc definition identity does not match the snapshot index: {entry.SourceFile}");

        return definition;
    }

    internal static async Task VerifyDefinitionsAsync(
        TDescSnapshotData snapshot,
        CancellationToken cancellationToken)
    {
        foreach (var entry in snapshot.Index.Classes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDefinitionEntry(snapshot, entry);
            var path = GetDefinitionPath(snapshot.Directory, entry.Sha256);
            PathGuard.EnsureWithinDirectory(path, snapshot.Directory, "TDesc definition shard");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            if (stream.Length != entry.Bytes)
                throw new CliInputException($"TDesc definition shard is missing or has changed length: {entry.SourceFile}");
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            if (!string.Equals(Convert.ToHexString(hash), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CliInputException($"TDesc definition shard digest does not match the snapshot index: {entry.SourceFile}");
        }
    }

    internal static TDescSnapshotReport ToReport(
        string directory,
        TDescSnapshotManifest manifest,
        TDescSnapshotIndex index,
        bool reused) => new(
        directory,
        manifest.GameVersion,
        manifest.Source,
        manifest.Files.Count,
        index.Classes.Count,
        manifest.SnapshotSha256,
        reused);

    private static async Task ReadSourceFileAsync(
        XmlReader reader,
        string sourceFile,
        string temporaryRoot,
        string definitionDirectory,
        HashSet<string> identities,
        List<TDescClassIndex> definitions,
        CancellationToken cancellationToken)
    {
        try
        {
            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "TuningRoot")
                throw new CliInputException($"TDesc file '{sourceFile}' must have a TuningRoot element.");

            if (reader.IsEmptyElement)
                throw new CliInputException($"TDesc file '{sourceFile}' contains no Instance or Module definitions.");

            var rootDepth = reader.Depth;
            var definitionCount = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == rootDepth)
                    break;
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != rootDepth + 1)
                {
                    if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && !string.IsNullOrWhiteSpace(reader.Value))
                        throw new CliInputException($"TDesc file '{sourceFile}' contains unsupported text content.");
                    continue;
                }

                if (reader.LocalName == "VersionHistory")
                {
                    SkipCurrentElement(reader);
                    continue;
                }
                if (reader.LocalName is not ("Instance" or "Module"))
                    throw new CliInputException($"TDesc file '{sourceFile}' has unsupported root child '{reader.LocalName}'.");

                var definition = await WriteDefinitionShardAsync(reader, sourceFile, temporaryRoot,
                    definitionDirectory, cancellationToken);
                var identity = GetIdentity(definition.DefinitionKind, definition.InstanceType, definition.Class, definition.Module);
                if (!identities.Add(identity))
                    throw new CliInputException($"TDesc source defines duplicate identity '{identity}'.");
                definitions.Add(definition.IndexEntry);
                definitionCount++;
            }

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element ||
                    reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && !string.IsNullOrWhiteSpace(reader.Value))
                    throw new CliInputException($"TDesc file '{sourceFile}' contains content after its TuningRoot element.");
            }

            if (definitionCount == 0)
                throw new CliInputException($"TDesc file '{sourceFile}' contains no Instance or Module definitions.");
        }
        catch (XmlException exception)
        {
            throw new CliInputException($"Invalid TDesc XML '{sourceFile}': {exception.Message}");
        }
    }

    private static async Task<TDescWrittenDefinition> WriteDefinitionShardAsync(
        XmlReader reader,
        string sourceFile,
        string temporaryRoot,
        string definitionDirectory,
        CancellationToken cancellationToken)
    {
        var kind = reader.LocalName;
        var instanceType = reader.GetAttribute("instance_type");
        var className = reader.GetAttribute("class");
        var module = reader.GetAttribute("module");
        if (kind == "Instance")
        {
            if (string.IsNullOrWhiteSpace(instanceType) || string.IsNullOrWhiteSpace(className) || string.IsNullOrWhiteSpace(module))
                throw new CliInputException($"TDesc Instance in '{sourceFile}' must declare instance_type, class, and module.");
        }
        else
        {
            module ??= reader.GetAttribute("name");
            if (string.IsNullOrWhiteSpace(module))
                throw new CliInputException($"TDesc Module in '{sourceFile}' must declare a name or module.");
            if (className is null)
                instanceType = null;
        }

        var line = GetLine(reader);
        var children = new List<TDescFieldSummary>();
        var temporaryShardPath = Path.Combine(temporaryRoot, $".pending-{Guid.NewGuid():N}.json");
        await using (var output = new FileStream(temporaryShardPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 32 * 1024, true))
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("sourceFile", sourceFile);
            writer.WriteString("definitionKind", kind);
            if (instanceType is null) writer.WriteNull("instanceType"); else writer.WriteString("instanceType", instanceType);
            if (className is null) writer.WriteNull("class"); else writer.WriteString("class", className);
            if (module is null) writer.WriteNull("module"); else writer.WriteString("module", module);
            writer.WriteNumber("line", line);
            writer.WritePropertyName("fields");
            writer.WriteStartArray();

            var definitionDepth = reader.Depth;
            if (!reader.IsEmptyElement)
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == definitionDepth)
                        break;
                    if (reader.NodeType != XmlNodeType.Element)
                    {
                        if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && !string.IsNullOrWhiteSpace(reader.Value))
                            throw new CliInputException($"TDesc definition '{module}:{className}' contains unsupported text content.");
                        continue;
                    }

                    if (reader.Depth == definitionDepth + 1)
                        children.Add(new TDescFieldSummary(reader.LocalName,
                            reader.GetAttribute("name"), reader.GetAttribute("type"), reader.GetAttribute("class")));
                    WriteCurrentElement(writer, reader, cancellationToken);
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            await output.FlushAsync(cancellationToken);
        }

        var byteCount = new FileInfo(temporaryShardPath).Length;
        if (byteCount is <= 0 or > MaximumTotalBytes)
            throw new CliInputException($"TDesc definition shard exceeds the supported size limit: {module}:{className}");
        string digest;
        await using (var shardStream = new FileStream(temporaryShardPath, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, true))
            digest = Convert.ToHexString(await SHA256.HashDataAsync(shardStream, cancellationToken)).ToLowerInvariant();
        var finalPath = Path.Combine(definitionDirectory, $"{digest}.json");
        if (File.Exists(finalPath))
            File.Delete(temporaryShardPath);
        else
            File.Move(temporaryShardPath, finalPath);

        var indexEntry = new TDescClassIndex(sourceFile, kind, instanceType, className, module,
            line, children, digest, byteCount);
        return new TDescWrittenDefinition(kind, instanceType, className, module, indexEntry);
    }

    private static void WriteCurrentElement(
        Utf8JsonWriter writer,
        XmlReader reader,
        CancellationToken cancellationToken)
    {
        var depth = reader.Depth;
        if (depth > MaximumXmlDepth)
            throw new CliInputException($"TDesc nesting exceeds the {MaximumXmlDepth} level limit.");

        writer.WriteStartObject();
        writer.WriteString("element", reader.LocalName);
        writer.WriteNumber("line", GetLine(reader));
        writer.WritePropertyName("attributes");
        writer.WriteStartObject();
        var attributes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (reader.HasAttributes)
        {
            while (reader.MoveToNextAttribute())
            {
                if (!OmittedDisplayAttributes.Contains(reader.LocalName))
                    attributes[reader.LocalName] = reader.Value;
            }
            reader.MoveToElement();
        }
        foreach (var attribute in attributes)
            writer.WriteString(attribute.Key, attribute.Value);
        writer.WriteEndObject();
        writer.WritePropertyName("children");
        writer.WriteStartArray();

        if (!reader.IsEmptyElement)
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                    break;
                if (reader.NodeType == XmlNodeType.Element)
                {
                    WriteCurrentElement(writer, reader, cancellationToken);
                    continue;
                }
                if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && !string.IsNullOrWhiteSpace(reader.Value))
                    throw new CliInputException($"TDesc element '{reader.LocalName}' contains unsupported text content.");
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void SkipCurrentElement(XmlReader reader)
    {
        if (reader.IsEmptyElement)
            return;
        var depth = reader.Depth;
        while (reader.Read())
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                return;
        throw new XmlException("Unexpected end of document while skipping VersionHistory.");
    }

    private static XmlReader CreateXmlReader(Stream stream, long maxCharacters) => XmlReader.Create(stream, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = maxCharacters,
        MaxCharactersFromEntities = 0,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        CloseInput = false
    });

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, true);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
    }

    private static void EnsureDefinitionEntry(TDescSnapshotData snapshot, TDescClassIndex entry)
    {
        if (!snapshot.Index.Classes.Any(candidate => ReferenceEquals(candidate, entry) || candidate == entry))
            throw new CliInputException("TDesc definition entry does not belong to the loaded snapshot.");
        if (!IsSha256(entry.Sha256) || entry.Bytes <= 0)
            throw new CliInputException("TDesc definition entry has an invalid digest or length.");
    }

    private static string GetDefinitionPath(string directory, string sha256) =>
        Path.Combine(directory, "definitions", $"{sha256}.json");

    private static string GetIdentity(string kind, string? instanceType, string? className, string? module) =>
        kind == "Instance"
            ? $"Instance\0{instanceType}\0{module}\0{className}"
            : $"{kind}\0{module}\0{className}";

    private static int GetLine(XmlReader reader) =>
        reader is IXmlLineInfo lineInfo && lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0;

    private static bool IsGameVersion(string? value) =>
        value is not null && System.Text.RegularExpressions.Regex.IsMatch(
            value, "^[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ComputeSnapshotDigest(
        string gameVersion,
        int parserFormat,
        IReadOnlyList<TDescSourceFile> sourceFiles,
        byte[] indexBytes)
    {
        var metadata = JsonSerializer.SerializeToUtf8Bytes(
            new TDescDigestInput(gameVersion, parserFormat,
                sourceFiles.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray()), JsonOptions);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(metadata);
        hash.AppendData([0]);
        hash.AppendData(indexBytes);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void DeleteOwnedTemporaryDirectory(string temporaryRoot, string outputRoot)
    {
        var fullTemporary = Path.GetFullPath(temporaryRoot);
        var parent = Path.GetDirectoryName(Path.GetFullPath(outputRoot))!;
        var relative = Path.GetRelativePath(parent, fullTemporary);
        if (Directory.Exists(fullTemporary) &&
            !Path.IsPathRooted(relative) &&
            relative.StartsWith($".{Path.GetFileName(outputRoot)}.", StringComparison.Ordinal) &&
            relative.EndsWith(".tmp", StringComparison.Ordinal) &&
            !relative.Contains(Path.DirectorySeparatorChar))
            Directory.Delete(fullTemporary, recursive: true);
    }

    private sealed record TDescWrittenDefinition(
        string DefinitionKind,
        string? InstanceType,
        string? Class,
        string? Module,
        TDescClassIndex IndexEntry);
}

internal enum TDescDefinitionKind
{
    Unknown,
    Instance,
    Module
}

internal sealed record TDescSnapshotManifest(
    int Format,
    int ParserFormat,
    string GameVersion,
    string Source,
    DateTimeOffset ImportedUtc,
    IReadOnlyList<TDescSourceFile> Files,
    string IndexSha256,
    string SnapshotSha256);

internal sealed record TDescSourceFile(string Path, long Bytes, string Sha256);
internal sealed record TDescSnapshotIndex(int Format, IReadOnlyList<TDescClassIndex> Classes);
internal sealed record TDescClassIndex(
    string SourceFile,
    string DefinitionKind,
    string? InstanceType,
    string? Class,
    string? Module,
    int Line,
    IReadOnlyList<TDescFieldSummary> Fields,
    string Sha256,
    long Bytes);
internal sealed record TDescFieldSummary(string Element, string? Name, string? Type, string? Class);
internal sealed record TDescDefinition(
    string SourceFile,
    string DefinitionKind,
    string? InstanceType,
    string? Class,
    string? Module,
    int Line,
    IReadOnlyList<TDescNode> Fields);
internal sealed record TDescNode(
    string Element,
    int Line,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyList<TDescNode> Children)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Name => Attributes.GetValueOrDefault("name");
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Type => Attributes.GetValueOrDefault("type");
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Class => Attributes.GetValueOrDefault("class");
}
internal sealed record TDescDigestInput(string GameVersion, int ParserFormat, IReadOnlyList<TDescSourceFile> Files);
internal sealed record TDescSnapshotData(string Directory, TDescSnapshotManifest Manifest, TDescSnapshotIndex Index);
internal sealed record TDescSnapshotReport(
    string Snapshot,
    string GameVersion,
    string Source,
    int SourceFileCount,
    int ClassCount,
    string Sha256,
    bool Reused);
