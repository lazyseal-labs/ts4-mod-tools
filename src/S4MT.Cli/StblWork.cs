using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlamaLogic.Packages;
using LlamaLogic.Packages.Models;

namespace S4MT.Cli;

internal sealed class StblWork
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    internal static bool IsStringTable(ResourceId resourceId) =>
        resourceId.Type == unchecked((uint)ResourceType.StringTable);

    internal void ValidatePayload(ResourceId resourceId, ReadOnlyMemory<byte> payload)
    {
        EnsureStringTable(resourceId);

        try
        {
            var table = StringTableModel.Decode(payload);
            foreach (var keyHash in table.KeyHashes)
                _ = table.Get(keyHash);
        }
        catch (Exception exception) when (exception is
            ArgumentException or EndOfStreamException or FormatException or IndexOutOfRangeException or
            InvalidDataException or KeyNotFoundException or NotSupportedException or OverflowException)
        {
            throw new CliInputException($"Invalid STBL resource {resourceId}: {exception.Message}");
        }
    }

    public async Task<StblExportReport> ExportAsync(
        string packagePath,
        ResourceId resourceId,
        string jsonPath,
        CancellationToken cancellationToken)
    {
        EnsureStringTable(resourceId);
        var fullPackagePath = Path.GetFullPath(packagePath);
        var fullJsonPath = Path.GetFullPath(jsonPath);
        PathGuard.EnsureDoesNotOverwrite(fullJsonPath, (fullPackagePath, "source package"));
        Directory.CreateDirectory(Path.GetDirectoryName(fullJsonPath)!);
        var temporaryPath = CreateTemporaryPath(fullJsonPath);

        try
        {
            StblDocument document;
            await using (var package = await DataBasePackedFile.FromPathAsync(fullPackagePath))
            {
                var key = ToPackageKey(resourceId);
                if (!await package.ContainsKeyAsync(key, cancellationToken))
                    throw new CliInputException($"Package does not contain STBL resource {resourceId}.");

                var table = await package.GetStringTableAsync(key, force: false, cancellationToken);
                var entries = table.KeyHashes
                    .Order()
                    .Select(hash => new StblEntry($"0x{hash:X8}", table.Get(hash)))
                    .ToArray();
                document = new StblDocument(1, resourceId.ToString(), entries) { Locale = GetLocale(resourceId) };
            }

            await using (var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                await JsonSerializer.SerializeAsync(output, document, JsonOptions, cancellationToken);

            File.Move(temporaryPath, fullJsonPath, overwrite: true);
            return new StblExportReport(
                fullPackagePath,
                fullJsonPath,
                resourceId.ToString(),
                document.Locale,
                document.Entries.Length);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<StblImportReport> ImportAsync(
        string packagePath,
        string jsonPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var fullPackagePath = Path.GetFullPath(packagePath);
        var fullJsonPath = Path.GetFullPath(jsonPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        PathGuard.EnsureDoesNotOverwrite(
            fullOutputPath,
            (fullPackagePath, "source package"),
            (fullJsonPath, "JSON input"));

        var document = await ReadDocumentAsync(fullJsonPath, cancellationToken);
        var resourceId = ResourceIdParser.ParseTgi(document.Tgi);
        EnsureStringTable(resourceId);
        var locale = GetLocale(resourceId);
        VerifyDeclaredLocale(document.Locale, locale);
        var requestedEntries = ParseEntries(document.Entries);
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
                    throw new CliInputException($"Source package does not contain STBL resource {resourceId}.");

                var originalKeys = (await source.GetKeysAsync(cancellationToken: cancellationToken)).ToHashSet();
                originalResourceCount = originalKeys.Count;
                var table = await source.GetStringTableAsync(key, force: false, cancellationToken);
                foreach (var currentHash in table.KeyHashes.ToArray())
                    if (!requestedEntries.ContainsKey(currentHash))
                        table.Delete(currentHash);
                foreach (var (hash, text) in requestedEntries)
                    table.Set(hash, text);

                await source.SetAsync(key, table, CompressionMode.Auto, cancellationToken);
                await source.SaveAsAsync(temporaryPath);

                await using var candidate = await DataBasePackedFile.FromPathAsync(temporaryPath);
                var candidateKeys = (await candidate.GetKeysAsync(cancellationToken: cancellationToken)).ToHashSet();
                if (!originalKeys.SetEquals(candidateKeys))
                    throw new InvalidDataException("STBL import changed the package resource-key set.");

                foreach (var unchangedKey in originalKeys.Where(existingKey => existingKey != key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var before = await source.GetAsync(unchangedKey, force: false, cancellationToken);
                    var after = await candidate.GetAsync(unchangedKey, force: false, cancellationToken);
                    if (!before.Span.SequenceEqual(after.Span))
                        throw new InvalidDataException($"STBL import changed unrelated resource {ToResourceId(unchangedKey)}.");
                }

                var verifiedTable = await candidate.GetStringTableAsync(key, force: false, cancellationToken);
                VerifyEntries(verifiedTable, requestedEntries, resourceId);
            }

            var digest = await ComputeSha256Async(temporaryPath, cancellationToken);
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            return new StblImportReport(
                fullPackagePath,
                fullOutputPath,
                resourceId.ToString(),
                locale,
                requestedEntries.Count,
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

    public async Task<StblBuildReport> BuildAsync(
        string jsonPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var fullJsonPath = Path.GetFullPath(jsonPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        PathGuard.EnsureDoesNotOverwrite(fullOutputPath, (fullJsonPath, "JSON input"));
        var document = await ReadDocumentAsync(fullJsonPath, cancellationToken);
        var resourceId = ResourceIdParser.ParseTgi(document.Tgi);
        EnsureStringTable(resourceId);
        var locale = GetLocale(resourceId);
        VerifyDeclaredLocale(document.Locale, locale);
        var entries = ParseEntries(document.Entries);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = CreateTemporaryPath(fullOutputPath);

        try
        {
            var table = new StringTableModel();
            foreach (var (hash, text) in entries)
                table.Set(hash, text);

            await using (var package = new DataBasePackedFile())
            {
                await package.SetAsync(ToPackageKey(resourceId), table, CompressionMode.Auto, cancellationToken);
                await package.SaveAsAsync(temporaryPath);
            }

            await using (var candidate = await DataBasePackedFile.FromPathAsync(temporaryPath))
            {
                var keys = await candidate.GetKeysAsync(cancellationToken: cancellationToken);
                if (keys.Count != 1 || keys[0] != ToPackageKey(resourceId))
                    throw new InvalidDataException("STBL build did not preserve its exact resource key.");
                var verifiedTable = await candidate.GetStringTableAsync(ToPackageKey(resourceId), false, cancellationToken);
                VerifyEntries(verifiedTable, entries, resourceId);
            }

            var digest = await ComputeSha256Async(temporaryPath, cancellationToken);
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            return new StblBuildReport(
                fullOutputPath,
                resourceId.ToString(),
                locale,
                entries.Count,
                digest,
                Verified: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static async Task<StblDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<StblDocument>(input, JsonOptions, cancellationToken)
            ?? throw new CliInputException("STBL JSON document is empty.");
        if (document.Format != 1)
            throw new CliInputException($"Unsupported STBL JSON format '{document.Format}'.");
        if (string.IsNullOrWhiteSpace(document.Tgi))
            throw new CliInputException("STBL JSON document must provide a TGI key.");
        if (document.Entries is null)
            throw new CliInputException("STBL JSON document must provide an entries array.");
        return document;
    }

    private static Dictionary<uint, string> ParseEntries(IReadOnlyList<StblEntry> entries)
    {
        var parsed = new Dictionary<uint, string>();
        foreach (var entry in entries)
        {
            if (entry.Text is null)
                throw new CliInputException($"STBL entry '{entry.Key}' has a null text value; use an empty string instead.");

            var digits = entry.Key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? entry.Key[2..]
                : entry.Key;
            if (digits.Length is < 1 or > 8 ||
                !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var key))
                throw new CliInputException($"STBL entry key '{entry.Key}' must be a 32-bit hexadecimal value.");
            if (!parsed.TryAdd(key, entry.Text))
                throw new CliInputException($"STBL JSON document contains duplicate key 0x{key:X8}.");
        }

        return parsed;
    }

    private static void VerifyEntries(
        StringTableModel table,
        IReadOnlyDictionary<uint, string> expected,
        ResourceId resourceId)
    {
        var actualKeys = table.KeyHashes.ToHashSet();
        if (!actualKeys.SetEquals(expected.Keys))
            throw new InvalidDataException($"STBL read-back key set did not match for {resourceId}.");

        foreach (var (key, expectedText) in expected)
            if (!string.Equals(table.Get(key), expectedText, StringComparison.Ordinal))
                throw new InvalidDataException($"STBL read-back text did not match for {resourceId} key 0x{key:X8}.");
    }

    private static void EnsureStringTable(ResourceId id)
    {
        if (!IsStringTable(id))
            throw new CliInputException($"Resource {id} is not a StringTable resource.");
    }

    private static string? GetLocale(ResourceId id)
    {
        try
        {
            return ToPackageKey(id).GetStringTableLocale().Name;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static void VerifyDeclaredLocale(string? declaredLocale, string? actualLocale)
    {
        if (declaredLocale is null)
            return;
        if (string.IsNullOrWhiteSpace(declaredLocale) ||
            !string.Equals(declaredLocale, actualLocale, StringComparison.OrdinalIgnoreCase))
            throw new CliInputException(
                $"STBL JSON locale '{declaredLocale}' does not match the locale '{actualLocale ?? "unknown"}' encoded in its TGI.");
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

    private sealed record StblDocument(int Format, string Tgi, StblEntry[] Entries)
    {
        public string? Locale { get; init; }
    }
    private sealed record StblEntry(string Key, string Text);
}
