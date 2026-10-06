namespace S4MT.Cli;

using System.Globalization;

public readonly record struct ResourceId(uint Type, uint Group, ulong Instance)
{
    public override string ToString() =>
        $"0x{Type:X8}:0x{Group:X8}:0x{Instance:X16}";
}

internal static class ResourceIdParser
{
    public static ResourceId Parse(string? type, string? group, string? instance) =>
        new(ParseHex32(type, "type"), ParseHex32(group, "group"), ParseHex64(instance, "instance"));

    public static ResourceId ParseTgi(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new CliInputException("A TGI key is required.");

        var parts = value.Split(':');
        if (parts.Length != 3)
            throw new CliInputException("TGI must use type:group:instance format.");

        return Parse(parts[0], parts[1], parts[2]);
    }

    public static uint ParseResourceType(string? value) => ParseHex32(value, "type");

    private static uint ParseHex32(string? value, string fieldName)
    {
        var digits = StripHexPrefix(value, fieldName, 8);
        if (!uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed))
            throw new CliInputException($"Manifest field '{fieldName}' must be a hexadecimal 32-bit value.");
        return parsed;
    }

    private static ulong ParseHex64(string? value, string fieldName)
    {
        var digits = StripHexPrefix(value, fieldName, 16);
        if (!ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var parsed))
            throw new CliInputException($"Manifest field '{fieldName}' must be a hexadecimal 64-bit value.");
        return parsed;
    }

    private static string StripHexPrefix(string? value, string fieldName, int maxDigits)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new CliInputException($"Manifest field '{fieldName}' is required.");

        var digits = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;

        if (digits.Length is < 1 || digits.Length > maxDigits)
            throw new CliInputException(
                $"Manifest field '{fieldName}' must contain between 1 and {maxDigits} hexadecimal digits.");

        return digits;
    }
}

public enum ResourceEncoding
{
    Bytes,
    Utf8
}

public sealed record EncodedResource(
    ResourceId Id,
    ReadOnlyMemory<byte> Content,
    string SourcePath,
    ResourceEncoding Encoding);

public sealed record BuildSpec(IReadOnlyList<EncodedResource> Resources);

public sealed record BuildReport(string Output, int ResourceCount, bool Verified, string Sha256);

public sealed record ResourceInfo(ResourceId Id, long SizeBytes);

public sealed record PackageReport(string Package, IReadOnlyList<ResourceInfo> Resources);

public sealed record PackageVerification(
    string Package,
    int ExpectedResourceCount,
    int ActualResourceCount,
    IReadOnlyList<string> MissingKeys,
    IReadOnlyList<string> ExtraKeys,
    IReadOnlyList<string> PayloadMismatchKeys,
    bool Valid);

public sealed record ProjectDiagnostic(string Severity, string Code, string Message, string? Path);

public sealed record ProjectValidationReport(
    bool Valid,
    string ProjectRoot,
    int ResourceCount,
    int ScriptArchiveCount,
    IReadOnlyList<ProjectDiagnostic> Diagnostics,
    DependencyGraphReport? DependencyGraph = null);

public sealed record DependencyGraphReport(
    IReadOnlyList<ResourceScanReport> Scans,
    IReadOnlyList<DependencyObservationReport> Observations,
    string? GameIndex,
    string? GameIndexFingerprint,
    bool? PrecedenceVerified);

public sealed record ResourceScanReport(string Source, string Coverage, IReadOnlyList<string> Reasons);

public sealed record DependencyObservationReport(
    string Source,
    string TargetKind,
    string? Target,
    string? Domain,
    string RawValue,
    string Evidence,
    string Location,
    string Resolution,
    IReadOnlyList<GameIndexProvider> Providers,
    string Requirement = "observed",
    string? Reason = null);

public sealed record ProjectInitializationReport(string ProjectFile, string PackageManifest, bool Created);

public sealed record ProjectBuildReport(
    string OutputDirectory,
    string? Package,
    int ResourceCount,
    int ScriptArchiveCount,
    string Fingerprint,
    bool Reused);

public sealed record ProjectBuildOutcome(ProjectValidationReport Validation, ProjectBuildReport? Build);

public sealed record GameIndexBuildReport(
    string Index,
    string GameRoot,
    string GameVersion,
    int PackageCount,
    long ResourceCount,
    string Fingerprint,
    bool Reused);

public sealed record GameIndexProvider(
    string Source,
    string Layer,
    string Pack,
    int LayerPriority,
    long PackageSizeBytes,
    DateTime PackageModifiedUtc);

public sealed record GameQueryReport(
    string Index,
    string Tgi,
    string Resolution,
    bool PrecedenceVerified,
    GameIndexProvider? UniqueProvider,
    IReadOnlyList<GameIndexProvider> Providers,
    string? Note);

public sealed record GameFindResource(string Tgi, string Source, string Layer, string Pack, int LayerPriority);

public sealed record GameFindReport(
    string Index,
    uint Type,
    long MatchCount,
    int Limit,
    bool Truncated,
    bool PrecedenceVerified,
    IReadOnlyList<GameFindResource> Resources);

public sealed record GameProviderLookupReport(
    string Index,
    long IndexResourceCount,
    bool PrecedenceVerified,
    IReadOnlyDictionary<string, IReadOnlyList<GameIndexProvider>> ProvidersByTgi,
    string Fingerprint,
    string GameVersion,
    IReadOnlySet<uint> ObservedTypes);

public sealed record GameConflictResource(
    string Tgi,
    int GameProviderCount,
    IReadOnlyList<GameIndexProvider> GameProviders,
    GameIndexProvider? EffectiveGameProvider);

public sealed record GameConflictReport(
    string Package,
    string Index,
    int PackageResourceCount,
    int ConflictingResourceCount,
    bool PrecedenceVerified,
    IReadOnlyList<GameConflictResource> Conflicts);

public sealed record PackageConflictReport(
    string LeftPackage,
    string RightPackage,
    int LeftResourceCount,
    int RightResourceCount,
    IReadOnlyList<string> SharedTgis);

public sealed record PackageMergeConflict(
    string Tgi,
    string PriorPackage,
    string IncomingPackage,
    string KeptPackage);

public sealed record PackageMergeReport(
    string OutputPackage,
    IReadOnlyList<string> InputPackages,
    int ResourceCount,
    string OnConflict,
    IReadOnlyList<PackageMergeConflict> Conflicts,
    string Sha256,
    bool Verified);

public sealed record ResourceExportReport(
    string Package,
    string Output,
    string Tgi,
    int PayloadBytes,
    string PayloadSha256);

public sealed record ResourceImportReport(
    string SourcePackage,
    string OutputPackage,
    string Tgi,
    int PayloadBytes,
    int PackageResourceCount,
    string PayloadSha256,
    string PackageSha256,
    bool Verified);

public sealed record StblExportReport(string Package, string Json, string Tgi, string? Locale, int EntryCount);

public sealed record StblImportReport(
    string SourcePackage,
    string OutputPackage,
    string Tgi,
    string? Locale,
    int EntryCount,
    int PackageResourceCount,
    string Sha256,
    bool Verified);

public sealed record StblBuildReport(string OutputPackage, string Tgi, string? Locale, int EntryCount, string Sha256, bool Verified);

public sealed record TuningExportReport(
    string Package,
    string Xml,
    string Tgi,
    string RootName,
    string? TuningName,
    string? TuningClass);

public sealed record TuningBuildReport(string OutputPackage, string Tgi, string RootName, string Sha256, bool Verified, TuningSchemaReport? SchemaValidation = null);

public sealed record TuningValidationReport(
    string Xml,
    string? Tgi,
    string RootName,
    string? TuningName,
    string? TuningClass,
    bool WellFormed,
    IReadOnlyList<string> SemanticChecks,
    IReadOnlyList<TuningNameHashReport> NameHashes,
    CombinedTuningSharedIndexReport? SharedIndexValidation,
    string Note,
    TuningSchemaReport? SchemaValidation = null)
{
    public bool Valid => WellFormed && (SharedIndexValidation?.Valid ?? true) && (SchemaValidation?.Valid ?? true);
}

public enum CombinedTuningSharedIndexStatus
{
    Valid,
    Invalid,
    Unsupported
}

public sealed record CombinedTuningSharedIndexIssue(string Code, string? Index);

public sealed record CombinedTuningSharedIndexReport(
    CombinedTuningSharedIndexStatus Status,
    int SharedTableCount,
    int DefinitionCount,
    int ReferenceCount,
    int MissingDefinitionIndexCount,
    int InvalidDefinitionIndexCount,
    int DuplicateDefinitionIndexCount,
    int MissingReferenceIndexCount,
    int InvalidReferenceIndexCount,
    int UnresolvedReferenceCount,
    int AmbiguousReferenceCount,
    int UnsupportedReasonCount,
    IReadOnlyList<CombinedTuningSharedIndexIssue> Examples)
{
    public bool Valid => Status is CombinedTuningSharedIndexStatus.Valid;

    public int IssueCount => MissingDefinitionIndexCount + InvalidDefinitionIndexCount +
        DuplicateDefinitionIndexCount + MissingReferenceIndexCount + InvalidReferenceIndexCount +
        UnresolvedReferenceCount + AmbiguousReferenceCount + UnsupportedReasonCount;
}

public sealed record TuningNameHashReport(
    string Name,
    string? DeclaredInstance,
    IReadOnlyList<TuningNameHashCandidate> Candidates);

public sealed record TuningNameHashCandidate(
    string Algorithm,
    string Hash,
    bool MatchesDeclaredInstance,
    bool MatchesResourceInstance);

public sealed record TuningImportReport(
    string SourcePackage,
    string OutputPackage,
    string Tgi,
    string RootName,
    int PackageResourceCount,
    string Sha256,
    bool Verified,
    TuningSchemaReport? SchemaValidation = null);

public sealed record SimDataColumnDescription(string? Name, string Type, ushort Flags);

public sealed record SimDataTableDescription(
    string? Name,
    string? SchemaName,
    uint? SchemaHash,
    int RowCount,
    int ColumnCount,
    IReadOnlyList<SimDataColumnDescription> Columns);

public sealed record SimDataInspectReport(
    string Package,
    string Tgi,
    string? ResourceName,
    string Version,
    int TableCount,
    int RowCount,
    IReadOnlyList<SimDataTableDescription> Tables);

public sealed record SimDataRoundtripReport(
    string SourcePackage,
    string OutputPackage,
    string Tgi,
    string? ResourceName,
    string Version,
    int TableCount,
    int RowCount,
    string Sha256,
    bool Verified);

public sealed record SimDataSetReport(
    string SourcePackage,
    string OutputPackage,
    string Tgi,
    string? ResourceName,
    string Version,
    string Table,
    int Row,
    string Column,
    string Type,
    string OldValue,
    string NewValue,
    int PackageResourceCount,
    string Sha256,
    bool Verified);

public sealed record ImageExportReport(
    string Package,
    string Png,
    string Tgi,
    string Codec,
    int Width,
    int Height,
    int PngBytes,
    int? MipLevel = null,
    int? MipCount = null);

public sealed record ImageBuildReport(
    string OutputPackage,
    string Tgi,
    int Width,
    int Height,
    long IdenticalPixels,
    long TotalPixels,
    int MaximumChannelDelta,
    double MeanChannelDelta,
    string Sha256,
    bool ReadBackVerified);

public sealed record ImageImportReport(
    string SourcePackage,
    string OutputPackage,
    string Tgi,
    int Width,
    int Height,
    int PackageResourceCount,
    long IdenticalPixels,
    long TotalPixels,
    int MaximumChannelDelta,
    double MeanChannelDelta,
    string Sha256,
    bool ReadBackVerified);

public sealed record ImagePlaneComparison(
    long IdenticalPixels,
    long TotalPixels,
    int MaximumChannelDelta,
    double MeanChannelDelta);

public sealed record RlesExportReport(
    string Package,
    string Bundle,
    string ColorPng,
    string MaskPng,
    string Tgi,
    int SourceWidth,
    int SourceHeight,
    int Width,
    int Height,
    int MipLevel,
    int MipCount,
    int ColorPngBytes,
    int MaskPngBytes);

public sealed record RlesBuildReport(
    string OutputPackage,
    string Bundle,
    string Tgi,
    int Width,
    int Height,
    int MipCount,
    int PixelVerifiedMipCount,
    bool AllMipsDecoded,
    ImagePlaneComparison Color,
    ImagePlaneComparison Mask,
    string Sha256,
    bool ReadBackVerified);

public sealed record RlesImportReport(
    string SourcePackage,
    string OutputPackage,
    string Bundle,
    string Tgi,
    int Width,
    int Height,
    int PackageResourceCount,
    int MipCount,
    int PixelVerifiedMipCount,
    bool AllMipsDecoded,
    ImagePlaneComparison Color,
    ImagePlaneComparison Mask,
    string Sha256,
    bool ReadBackVerified);

public sealed class CliInputException(string message) : Exception(message);
