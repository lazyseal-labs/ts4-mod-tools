using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using LlamaLogic.Packages.Cryptography;
using LlamaLogic.Packages;

namespace S4MT.Cli;

internal sealed record TuningSchemaPayloadAnalysis(
    TuningSchemaReport? Validation,
    IReadOnlyList<TuningReferenceObservation> References);

internal sealed class TuningWork
{
    private const long MaxXmlCharacters = 32 * 1024 * 1024;
    private const int MaxSharedTableEntries = 250_000;
    private const int MaxSharedIndexCharacters = 64;
    private const int MaxSharedIndexExamples = 32;

    public async Task<TuningBuildReport> BuildAsync(
        ResourceId resourceId,
        string xmlPath,
        string outputPath,
        CancellationToken cancellationToken,
        TuningSchemaValidator? schemaValidator = null)
    {
        EnsureTuningType(resourceId);
        var fullXmlPath = Path.GetFullPath(xmlPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        PathGuard.EnsureDoesNotOverwrite(fullOutputPath, (fullXmlPath, "XML input"));
        var inputXml = await File.ReadAllTextAsync(fullXmlPath, cancellationToken);
        var parsedInput = ParseXml(inputXml, resourceId);
        EnsureSharedTableIndexesValid(parsedInput.Document, resourceId);
        var schemaValidation = schemaValidator is null ? null :
            await schemaValidator.ValidateAsync(parsedInput.Document, cancellationToken);
        if (schemaValidation is { Valid: false })
            throw new CliInputException("Tuning XML failed pinned schema validation; package was not written.");
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = CreateTemporaryPath(fullOutputPath);

        try
        {
            await using (var package = new DataBasePackedFile())
            {
                package.Set(ToPackageKey(resourceId), parsedInput.Document, CompressionMode.Auto);
                await package.SaveAsAsync(temporaryPath);
            }

            await using (var candidate = await DataBasePackedFile.FromPathAsync(temporaryPath))
            {
                var keys = await candidate.GetKeysAsync(cancellationToken: cancellationToken);
                if (keys.Count != 1 || keys[0] != ToPackageKey(resourceId))
                    throw new InvalidDataException("Tuning build did not preserve its exact resource key.");
                var verifiedXml = await candidate.GetXmlAsync(ToPackageKey(resourceId), false, cancellationToken);
                if (!XmlEquivalent(parsedInput.Document, verifiedXml))
                    throw new InvalidDataException($"Tuning XML read-back did not match resource {resourceId}: {DescribeXmlDifference(parsedInput.Document, verifiedXml)}");
            }

            var digest = await ComputeSha256Async(temporaryPath, cancellationToken);
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            return new TuningBuildReport(
                fullOutputPath,
                resourceId.ToString(),
                parsedInput.Metadata.RootName,
                digest,
                Verified: true,
                SchemaValidation: schemaValidation);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<TuningExportReport> ExportAsync(
        string packagePath,
        ResourceId resourceId,
        string xmlPath,
        CancellationToken cancellationToken)
    {
        EnsureTuningType(resourceId);
        var fullPackagePath = Path.GetFullPath(packagePath);
        var fullXmlPath = Path.GetFullPath(xmlPath);
        PathGuard.EnsureDoesNotOverwrite(fullXmlPath, (fullPackagePath, "source package"));
        Directory.CreateDirectory(Path.GetDirectoryName(fullXmlPath)!);
        var temporaryPath = CreateTemporaryPath(fullXmlPath);

        try
        {
            string xml;
            XmlMetadata metadata;
            await using (var package = await DataBasePackedFile.FromPathAsync(fullPackagePath))
            {
                var key = ToPackageKey(resourceId);
                if (!await package.ContainsKeyAsync(key, cancellationToken))
                    throw new CliInputException($"Package does not contain tuning resource {resourceId}.");

                var document = await package.GetXmlAsync(key, force: false, cancellationToken);
                xml = document.ToString(SaveOptions.DisableFormatting);
                metadata = GetMetadata(document);
            }

            await File.WriteAllTextAsync(temporaryPath, xml, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, fullXmlPath, overwrite: true);
            return new TuningExportReport(
                fullPackagePath,
                fullXmlPath,
                resourceId.ToString(),
                metadata.RootName,
                metadata.Name,
                metadata.Class);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<TuningValidationReport> ValidateAsync(
        string xmlPath,
        ResourceId? resourceId,
        CancellationToken cancellationToken,
        TuningSchemaValidator? schemaValidator = null)
    {
        if (resourceId is { } id)
            EnsureTuningType(id);

        var fullXmlPath = Path.GetFullPath(xmlPath);
        var xml = await File.ReadAllTextAsync(fullXmlPath, cancellationToken);
        var parsed = ParseXml(xml, resourceId);
        var nameHashes = DescribeNameHashes(parsed.Metadata, resourceId);
        var sharedIndexValidation = ValidateCombinedTuningSharedIndexes(parsed.Document);
        var schemaValidation = schemaValidator is null ? null :
            await schemaValidator.ValidateAsync(parsed.Document, cancellationToken);
        var semanticChecks = DescribeSemanticChecks(parsed.Metadata, resourceId, nameHashes).ToList();
        if (sharedIndexValidation is not null)
            semanticChecks.Add($"combined_shared_table_indices_{sharedIndexValidation.Status.ToString().ToLowerInvariant()}");
        return new TuningValidationReport(
            fullXmlPath,
            resourceId?.ToString(),
            parsed.Metadata.RootName,
            parsed.Metadata.Name,
            parsed.Metadata.Class,
            WellFormed: true,
            semanticChecks,
            nameHashes,
            sharedIndexValidation,
            Note: "Calculated FNV32/FNV64 candidates for the root name and compared declared/root IDs when available. CombinedTuning shared-table indices are checked only for XML roots named 'combined'; external resource/STBL references, binary CombinedTuning, and SimData pairing are not validated.",
            SchemaValidation: schemaValidation);
    }

    internal CombinedTuningSharedIndexReport? ValidatePayload(ResourceId resourceId, ReadOnlyMemory<byte> payload)
    {
        EnsureTuningType(resourceId);
        if (payload.Length > MaxXmlCharacters)
            throw new CliInputException("Tuning XML exceeds the 32 MiB input limit.");

        string xml;
        try
        {
            xml = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(payload.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new CliInputException($"Tuning resource {resourceId} is not valid UTF-8: {exception.Message}");
        }

        if (xml.StartsWith('\uFEFF'))
            xml = xml[1..];

        try
        {
            var parsed = ParseXml(xml, resourceId);
            return ValidateCombinedTuningSharedIndexes(parsed.Document);
        }
        catch (XmlException exception)
        {
            throw new CliInputException($"Invalid tuning XML for resource {resourceId}: {exception.Message}");
        }
    }

    internal async Task<TuningSchemaReport?> ValidateSchemaPayloadAsync(
        ResourceId resourceId,
        ReadOnlyMemory<byte> payload,
        TuningSchemaValidator? schemaValidator,
        CancellationToken cancellationToken)
    {
        return (await AnalyzeSchemaPayloadAsync(resourceId, payload, schemaValidator, cancellationToken)).Validation;
    }

    internal async Task<TuningSchemaPayloadAnalysis> AnalyzeSchemaPayloadAsync(
        ResourceId resourceId,
        ReadOnlyMemory<byte> payload,
        TuningSchemaValidator? schemaValidator,
        CancellationToken cancellationToken)
    {
        if (schemaValidator is null)
            return new TuningSchemaPayloadAnalysis(null, []);
        if (payload.Length > MaxXmlCharacters)
            throw new CliInputException("Tuning XML exceeds the 32 MiB input limit.");
        string xml;
        try
        {
            xml = new UTF8Encoding(false, true).GetString(payload.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new CliInputException($"Tuning resource {resourceId} is not valid UTF-8: {exception.Message}");
        }
        if (xml.StartsWith('\uFEFF'))
            xml = xml[1..];
        try
        {
            var parsed = ParseXml(xml, resourceId);
            var validation = await schemaValidator.ValidateAsync(parsed.Document, cancellationToken);
            var references = await schemaValidator.ExtractTopLevelReferencesAsync(parsed.Document, cancellationToken);
            return new TuningSchemaPayloadAnalysis(validation, references);
        }
        catch (XmlException exception)
        {
            throw new CliInputException($"Invalid tuning XML for resource {resourceId}: {exception.Message}");
        }
    }

    public async Task<TuningImportReport> ImportAsync(
        string packagePath,
        ResourceId resourceId,
        string xmlPath,
        string outputPath,
        CancellationToken cancellationToken,
        TuningSchemaValidator? schemaValidator = null)
    {
        EnsureTuningType(resourceId);
        var fullPackagePath = Path.GetFullPath(packagePath);
        var fullXmlPath = Path.GetFullPath(xmlPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        PathGuard.EnsureDoesNotOverwrite(
            fullOutputPath,
            (fullPackagePath, "source package"),
            (fullXmlPath, "XML input"));

        var inputXml = await File.ReadAllTextAsync(fullXmlPath, cancellationToken);
        var parsedInput = ParseXml(inputXml, resourceId);
        EnsureSharedTableIndexesValid(parsedInput.Document, resourceId);
        var schemaValidation = schemaValidator is null ? null :
            await schemaValidator.ValidateAsync(parsedInput.Document, cancellationToken);
        if (schemaValidation is { Valid: false })
            throw new CliInputException("Tuning XML failed pinned schema validation; package was not written.");
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
                    throw new CliInputException($"Source package does not contain tuning resource {resourceId}.");

                var originalKeys = (await source.GetKeysAsync(cancellationToken: cancellationToken)).ToHashSet();
                originalResourceCount = originalKeys.Count;
                _ = await source.GetXmlAsync(key, force: false, cancellationToken);
                source.Set(key, parsedInput.Document, CompressionMode.Auto);
                await source.SaveAsAsync(temporaryPath);

                await using var candidate = await DataBasePackedFile.FromPathAsync(temporaryPath);
                var candidateKeys = (await candidate.GetKeysAsync(cancellationToken: cancellationToken)).ToHashSet();
                if (!originalKeys.SetEquals(candidateKeys))
                    throw new InvalidDataException("Tuning import changed the package resource-key set.");

                foreach (var unchangedKey in originalKeys.Where(existingKey => existingKey != key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var before = await source.GetAsync(unchangedKey, force: false, cancellationToken);
                    var after = await candidate.GetAsync(unchangedKey, force: false, cancellationToken);
                    if (!before.Span.SequenceEqual(after.Span))
                        throw new InvalidDataException($"Tuning import changed unrelated resource {ToResourceId(unchangedKey)}.");
                }

                var verifiedXml = await candidate.GetXmlAsync(key, force: false, cancellationToken);
                if (!XmlEquivalent(parsedInput.Document, verifiedXml))
                    throw new InvalidDataException($"Tuning XML read-back did not match resource {resourceId}: {DescribeXmlDifference(parsedInput.Document, verifiedXml)}");
            }

            var digest = await ComputeSha256Async(temporaryPath, cancellationToken);
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            return new TuningImportReport(
                fullPackagePath,
                fullOutputPath,
                resourceId.ToString(),
                parsedInput.Metadata.RootName,
                originalResourceCount,
                digest,
                Verified: true,
                SchemaValidation: schemaValidation);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static (XDocument Document, XmlMetadata Metadata) ParseXml(string xml, ResourceId? resourceId)
    {
        if (Encoding.UTF8.GetByteCount(xml) > MaxXmlCharacters)
            throw new CliInputException("Tuning XML exceeds the 32 MiB input limit.");

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlCharacters,
            MaxCharactersFromEntities = 0
        };
        using var input = new StringReader(xml);
        using var reader = XmlReader.Create(input, settings);
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        return (document, GetMetadata(document));
    }

    private static CombinedTuningSharedIndexReport? ValidateCombinedTuningSharedIndexes(XDocument document)
    {
        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "combined", StringComparison.Ordinal))
            return null;

        bool HasName(XElement element, string name) =>
            element.Name.Namespace == root.Name.Namespace &&
            string.Equals(element.Name.LocalName, name, StringComparison.Ordinal);

        var sharedTables = root.Elements().Where(element => HasName(element, "g")).ToArray();
        var definitions = new Dictionary<string, int>(StringComparer.Ordinal);
        var examples = new List<CombinedTuningSharedIndexIssue>(MaxSharedIndexExamples);
        var definitionCount = 0;
        var referenceCount = 0;
        var missingDefinitionIndexCount = 0;
        var invalidDefinitionIndexCount = 0;
        var duplicateDefinitionIndexCount = 0;
        var missingReferenceIndexCount = 0;
        var invalidReferenceIndexCount = 0;
        var unresolvedReferenceCount = 0;
        var ambiguousReferenceCount = 0;
        var unsupportedReasonCount = 0;

        void AddExample(string code, string? index)
        {
            if (examples.Count < MaxSharedIndexExamples)
                examples.Add(new CombinedTuningSharedIndexIssue(code, index));
        }

        if (sharedTables.Length > 1)
        {
            unsupportedReasonCount++;
            AddExample("unsupported_shared_table_layout", null);
            definitionCount = sharedTables.Sum(table => table.Elements().Count());
        }
        else if (sharedTables.Length == 1)
        {
            foreach (var definition in sharedTables[0].Elements())
            {
                definitionCount++;
                var index = (string?)definition.Attribute("x");
                if (index is null)
                {
                    missingDefinitionIndexCount++;
                    AddExample("missing_definition_index", null);
                    continue;
                }
                if (index.Length == 0 || index.Length > MaxSharedIndexCharacters)
                {
                    invalidDefinitionIndexCount++;
                    AddExample("invalid_definition_index", index.Length <= MaxSharedIndexCharacters ? index : null);
                    continue;
                }

                if (definitionCount > MaxSharedTableEntries)
                {
                    if (unsupportedReasonCount == 0)
                        AddExample("shared_index_limit_exceeded", null);
                    unsupportedReasonCount = 1;
                    continue;
                }

                if (definitions.TryGetValue(index, out var occurrences))
                {
                    definitions[index] = occurrences + 1;
                    duplicateDefinitionIndexCount++;
                    AddExample("duplicate_definition_index", index);
                }
                else
                {
                    definitions.Add(index, 1);
                }
            }
        }

        var referenceChecksComplete = sharedTables.Length <= 1 && unsupportedReasonCount == 0;
        foreach (var reference in root.Descendants().Where(element => HasName(element, "r")))
        {
            referenceCount++;
            var index = (string?)reference.Attribute("x");
            if (index is null)
            {
                missingReferenceIndexCount++;
                AddExample("missing_reference_index", null);
                continue;
            }
            if (index.Length == 0 || index.Length > MaxSharedIndexCharacters)
            {
                invalidReferenceIndexCount++;
                AddExample("invalid_reference_index", index.Length <= MaxSharedIndexCharacters ? index : null);
                continue;
            }
            if (!referenceChecksComplete)
                continue;
            if (!definitions.TryGetValue(index, out var occurrences))
            {
                unresolvedReferenceCount++;
                AddExample("unresolved_reference_index", index);
            }
            else if (occurrences > 1)
            {
                ambiguousReferenceCount++;
                AddExample("ambiguous_reference_index", index);
            }
        }

        var knownIssueCount = missingDefinitionIndexCount + invalidDefinitionIndexCount +
            duplicateDefinitionIndexCount + missingReferenceIndexCount + invalidReferenceIndexCount +
            unresolvedReferenceCount + ambiguousReferenceCount;
        var status = knownIssueCount > 0
            ? CombinedTuningSharedIndexStatus.Invalid
            : unsupportedReasonCount > 0
                ? CombinedTuningSharedIndexStatus.Unsupported
                : CombinedTuningSharedIndexStatus.Valid;

        return new CombinedTuningSharedIndexReport(
            status,
            sharedTables.Length,
            definitionCount,
            referenceCount,
            missingDefinitionIndexCount,
            invalidDefinitionIndexCount,
            duplicateDefinitionIndexCount,
            missingReferenceIndexCount,
            invalidReferenceIndexCount,
            unresolvedReferenceCount,
            ambiguousReferenceCount,
            unsupportedReasonCount,
            examples);
    }

    private static void EnsureSharedTableIndexesValid(XDocument document, ResourceId resourceId)
    {
        var validation = ValidateCombinedTuningSharedIndexes(document);
        if (validation is not null && validation.Status is not CombinedTuningSharedIndexStatus.Valid)
            throw new CliInputException(DescribeSharedIndexValidationFailure(resourceId, validation));
    }

    internal static string DescribeSharedIndexValidationFailure(
        ResourceId resourceId,
        CombinedTuningSharedIndexReport validation)
    {
        var examples = string.Join(", ", validation.Examples.Select(issue =>
            issue.Index is null ? issue.Code : $"{issue.Code} (x='{issue.Index}')"));
        var status = validation.Status.ToString().ToLowerInvariant();
        var detail = examples.Length == 0 ? "no example retained" : $"examples: {examples}";
        return $"CombinedTuning XML resource {resourceId} has {status} shared-table indexes: " +
               $"{validation.IssueCount} issue(s), {validation.DefinitionCount} definitions, " +
               $"{validation.ReferenceCount} references; {detail}.";
    }

    private static XmlMetadata GetMetadata(XDocument document)
    {
        var root = document.Root ?? throw new CliInputException("Tuning XML has no root element.");
        return new XmlMetadata(
            root.Name.LocalName,
            Attribute(root, "n"),
            Attribute(root, "c"),
            Attribute(root, "s"));
    }

    private static IReadOnlyList<TuningNameHashReport> DescribeNameHashes(
        XmlMetadata metadata,
        ResourceId? resourceId)
    {
        if (string.IsNullOrWhiteSpace(metadata.Name))
            return [];

        var fnv32 = Fnv32.GetHash(metadata.Name);
        var fnv64 = Fnv64.GetHash(metadata.Name);
        var candidates = new[]
        {
            MakeHashCandidate("FNV32", fnv32, metadata.Instance, resourceId),
            MakeHashCandidate("FNV32-high-bit", fnv32 | 0x80000000U, metadata.Instance, resourceId),
            MakeHashCandidate("FNV64", fnv64, metadata.Instance, resourceId),
            MakeHashCandidate("FNV64-high-bit", fnv64 | 0x8000000000000000UL, metadata.Instance, resourceId)
        };
        return [new TuningNameHashReport(metadata.Name, metadata.Instance, candidates)];
    }

    private static TuningNameHashCandidate MakeHashCandidate(
        string algorithm,
        ulong hash,
        string? declaredInstance,
        ResourceId? resourceId)
    {
        var hex = $"0x{hash:X16}";
        var matchesDeclaredInstance = TryParseInstance(declaredInstance, out var declared) && declared == hash;
        var matchesResourceInstance = resourceId is { } id && id.Instance == hash;
        return new TuningNameHashCandidate(algorithm, hex, matchesDeclaredInstance, matchesResourceInstance);
    }

    private static IReadOnlyList<string> DescribeSemanticChecks(
        XmlMetadata metadata,
        ResourceId? resourceId,
        IReadOnlyList<TuningNameHashReport> nameHashes)
    {
        var checks = new List<string>();
        if (nameHashes.Count == 0)
            checks.Add("root_name_hash_not_checked:missing_n_attribute");
        else if (!nameHashes[0].Candidates.Any(candidate => candidate.MatchesDeclaredInstance))
            checks.Add("root_name_hash_no_candidate_matches_s");
        else
            checks.Add("root_name_hash_candidate_matches_s");

        if (resourceId is { } id)
        {
            if (TryParseInstance(metadata.Instance, out var declaredInstance))
                checks.Add(declaredInstance == id.Instance
                    ? "declared_s_matches_resource_instance"
                    : "declared_s_differs_from_resource_instance");
            else
                checks.Add("declared_s_not_compared:missing_or_non_numeric");

            if (nameHashes.Count > 0 && nameHashes[0].Candidates.Any(candidate => candidate.MatchesResourceInstance))
                checks.Add("root_name_hash_candidate_matches_resource_instance");
            else if (nameHashes.Count > 0)
                checks.Add("root_name_hash_no_candidate_matches_resource_instance");
        }

        return checks;
    }

    private static bool TryParseInstance(string? value, out ulong instance)
    {
        instance = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ulong.TryParse(value.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out instance);
        return ulong.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out instance);
    }

    private static bool XmlEquivalent(XDocument left, XDocument right) =>
        XNode.DeepEquals(NormalizeXml(left), NormalizeXml(right));

    private static string DescribeXmlDifference(XDocument left, XDocument right)
    {
        var leftText = NormalizeXml(left).ToString(SaveOptions.DisableFormatting);
        var rightText = NormalizeXml(right).ToString(SaveOptions.DisableFormatting);
        var firstDifference = 0;
        while (firstDifference < leftText.Length && firstDifference < rightText.Length &&
               leftText[firstDifference] == rightText[firstDifference])
            firstDifference++;
        var start = Math.Max(0, firstDifference - 60);
        var leftContext = leftText.Substring(start, Math.Min(120, leftText.Length - start));
        var rightContext = rightText.Substring(start, Math.Min(120, rightText.Length - start));
        return $"normalized lengths {leftText.Length}/{rightText.Length}; first difference at {firstDifference}; expected '{leftContext}'; actual '{rightContext}'";
    }

    private static XDocument NormalizeXml(XDocument source)
    {
        var normalized = new XDocument(source);
        foreach (var formattingText in normalized.Nodes().OfType<XText>()
                     .Where(text => string.IsNullOrWhiteSpace(text.Value)).ToArray())
            formattingText.Remove();
        if (normalized.Root is { } root)
            NormalizeElement(root, inheritedSpacePreserve: false);
        return normalized;
    }

    private static void NormalizeElement(XElement element, bool inheritedSpacePreserve)
    {
        var spaceAttribute = (string?)element.Attribute(XNamespace.Xml + "space");
        var preserveSpace = spaceAttribute switch
        {
            "preserve" => true,
            "default" => false,
            _ => inheritedSpacePreserve
        };

        if (!preserveSpace && element.Elements().Any())
            foreach (var formattingText in element.Nodes().OfType<XText>()
                         .Where(text => string.IsNullOrWhiteSpace(text.Value)).ToArray())
                formattingText.Remove();

        foreach (var child in element.Elements())
            NormalizeElement(child, preserveSpace);
    }

    private static string? Attribute(XElement element, string name) =>
        (string?)element.Attribute(name);

    internal static bool IsTuningType(ResourceId id)
    {
        var name = Enum.GetName(typeof(ResourceType), unchecked((ResourceType)id.Type));
        return name is not null && (name.EndsWith("Tuning", StringComparison.Ordinal) ||
                                    string.Equals(name, "CombinedTuning", StringComparison.Ordinal));
    }

    private static void EnsureTuningType(ResourceId id)
    {
        if (!IsTuningType(id))
            throw new CliInputException($"Resource {id} is not a recognized tuning resource type.");
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

    private sealed record XmlMetadata(string RootName, string? Name, string? Class, string? Instance);
}
