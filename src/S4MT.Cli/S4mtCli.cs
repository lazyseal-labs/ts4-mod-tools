using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace S4MT.Cli;

internal static class S4mtCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly PackageWork PackageWork = new();
    private static readonly PackageMergeWork PackageMerge = new();
    private static readonly ResourceWork Resources = new();
    private static readonly ModProjectWork ProjectOperations = new();
    private static readonly GameCatalogWork GameCatalog = new();
    private static readonly StblWork Stbl = new();
    private static readonly CaspWork Casp = new();
    private static readonly TuningWork Tuning = new();
    private static readonly CombinedTuningWork CombinedTuning = new();
    private static readonly SimDataWork SimData = new();
    private static readonly ImageWork Image = new();
    private static readonly ConflictWork Conflicts = new(GameCatalog);

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal) ||
                args.Contains("-h", StringComparer.Ordinal) || args[0] == "help")
            {
                Console.Out.WriteLine(HelpText);
                return 0;
            }

            if (args.Length == 1 && args[0] == "--version")
            {
                var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.1.0";
                WriteJson(new { schemaVersion = 1, command = "version", version });
                return 0;
            }

            CommandCatalog.ValidateInvocation(args);
            return args[0] switch
            {
                "build" => await BuildAsync(args[1..], cancellationToken),
                "package" => await PackageAsync(args[1..], cancellationToken),
                "resource" => await ResourceAsync(args[1..], cancellationToken),
                "inspect" => await InspectAsync(args[1..], cancellationToken),
                "verify" => await VerifyAsync(args[1..], cancellationToken),
                "project" => await ProjectAsync(args[1..], cancellationToken),
                "game" => await GameAsync(args[1..], cancellationToken),
                "tdesc" => await TDescAsync(args[1..], cancellationToken),
                "stbl" => await StblAsync(args[1..], cancellationToken),
                "casp" => await CaspAsync(args[1..], cancellationToken),
                "tuning" => await TuningAsync(args[1..], cancellationToken),
                "simdata" => await SimDataAsync(args[1..], cancellationToken),
                "image" => await ImageAsync(args[1..], cancellationToken),
                "conflict" => await ConflictAsync(args[1..], cancellationToken),
                _ => throw new CliInputException($"Unknown command '{args[0]}'.")
            };
        }
        catch (CliInputException exception)
        {
            WriteError("invalid_input", exception.Message);
            return 2;
        }
        catch (OperationCanceledException)
        {
            WriteError("cancelled", "Operation cancelled.");
            return 130;
        }
        catch (Exception exception)
        {
            WriteError("operation_failed", exception.Message);
            return 1;
        }
    }

    private static async Task<int> BuildAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, "--manifest", "--output");
        var manifestPath = RequireOption(options, "--manifest");
        var outputPath = RequireOption(options, "--output");
        var spec = await ManifestReader.ReadAsync(manifestPath, cancellationToken, outputPath);
        var report = await PackageWork.BuildAsync(spec, outputPath, cancellationToken);

        WriteJson(new
        {
            schemaVersion = 1,
            command = "build",
            output = report.Output,
            resourceCount = report.ResourceCount,
            verified = report.Verified,
            sha256 = report.Sha256
        });
        return 0;
    }

    private static async Task<int> VerifyAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, "--manifest", "--package");
        var manifestPath = RequireOption(options, "--manifest");
        var packagePath = RequireOption(options, "--package");
        var spec = await ManifestReader.ReadAsync(manifestPath, cancellationToken);
        var report = await PackageWork.VerifyAsync(packagePath, spec, cancellationToken);

        WriteJson(new
        {
            schemaVersion = 1,
            command = "verify",
            package = report.Package,
            valid = report.Valid,
            expectedResourceCount = report.ExpectedResourceCount,
            actualResourceCount = report.ActualResourceCount,
            missingKeys = report.MissingKeys,
            extraKeys = report.ExtraKeys,
            payloadMismatchKeys = report.PayloadMismatchKeys
        });
        return report.Valid ? 0 : 1;
    }

    private static async Task<int> PackageAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] != "merge")
            throw new CliInputException("Package command requires merge.");

        var options = ParseOptions(args[1..], "--manifest", "--output");
        var report = await PackageMerge.MergeAsync(
            RequireOption(options, "--manifest"),
            RequireOption(options, "--output"),
            cancellationToken);
        WriteJson(new
        {
            schemaVersion = 1,
            command = "package.merge",
            outputPackage = report.OutputPackage,
            inputPackages = report.InputPackages,
            resourceCount = report.ResourceCount,
            onConflict = report.OnConflict,
            conflicts = report.Conflicts,
            sha256 = report.Sha256,
            verified = report.Verified
        });
        return 0;
    }

    private static async Task<int> ResourceAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
            throw new CliInputException("Resource command requires export or import.");

        switch (args[0])
        {
            case "export":
            {
                var options = ParseOptions(args[1..], "--package", "--tgi", "--output");
                var report = await Resources.ExportAsync(
                    RequireOption(options, "--package"),
                    ResourceIdParser.ParseTgi(RequireOption(options, "--tgi")),
                    RequireOption(options, "--output"),
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "resource.export",
                    package = report.Package,
                    output = report.Output,
                    tgi = report.Tgi,
                    payloadBytes = report.PayloadBytes,
                    payloadSha256 = report.PayloadSha256
                });
                return 0;
            }
            case "import":
            {
                var options = ParseOptions(args[1..], "--package", "--tgi", "--payload", "--output");
                var report = await Resources.ImportAsync(
                    RequireOption(options, "--package"),
                    ResourceIdParser.ParseTgi(RequireOption(options, "--tgi")),
                    RequireOption(options, "--payload"),
                    RequireOption(options, "--output"),
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "resource.import",
                    sourcePackage = report.SourcePackage,
                    outputPackage = report.OutputPackage,
                    tgi = report.Tgi,
                    payloadBytes = report.PayloadBytes,
                    packageResourceCount = report.PackageResourceCount,
                    payloadSha256 = report.PayloadSha256,
                    packageSha256 = report.PackageSha256,
                    typedValidation = false,
                    verified = report.Verified
                });
                return 0;
            }
            default:
                throw new CliInputException($"Unknown resource operation '{args[0]}'.");
        }
    }

    private static async Task<int> ProjectAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
            throw new CliInputException("Project command requires init, validate, or build.");

        return args[0] switch
        {
            "init" => ProjectInit(args[1..]),
            "validate" => await ProjectValidateAsync(args[1..], cancellationToken),
            "build" => await ProjectBuildAsync(args[1..], cancellationToken),
            _ => throw new CliInputException($"Unknown project operation '{args[0]}'.")
        };
    }

    private static int ProjectInit(string[] args)
    {
        var options = ParseOptions(args, "--name", "--path");
        var name = RequireOption(options, "--name");
        var path = RequireOption(options, "--path");
        var report = ProjectOperations.Initialize(path, name);

        WriteJson(new
        {
            schemaVersion = 1,
            command = "project.init",
            projectFile = report.ProjectFile,
            packageManifest = report.PackageManifest,
            created = report.Created
        });
        return 0;
    }

    private static async Task<int> ProjectValidateAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, "--project", "--game-index");
        var projectPath = RequireOption(options, "--project");
        options.TryGetValue("--game-index", out var gameIndex);
        var report = await ProjectOperations.ValidateAsync(projectPath, cancellationToken, gameIndex);
        WriteProjectValidation("project.validate", report);
        return report.Valid ? 0 : 1;
    }

    private static async Task<int> ProjectBuildAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, "--project", "--output-root", "--game-index");
        var projectPath = RequireOption(options, "--project");
        var outputRoot = RequireOption(options, "--output-root");
        options.TryGetValue("--game-index", out var gameIndex);
        var outcome = await ProjectOperations.BuildAsync(projectPath, outputRoot, cancellationToken, gameIndex);
        if (outcome.Build is null)
        {
            WriteProjectValidation("project.build", outcome.Validation);
            return 1;
        }

        WriteJson(new
        {
            schemaVersion = 1,
            command = "project.build",
            valid = true,
            outputDirectory = outcome.Build.OutputDirectory,
            package = outcome.Build.Package,
            resourceCount = outcome.Build.ResourceCount,
            scriptArchiveCount = outcome.Build.ScriptArchiveCount,
            fingerprint = outcome.Build.Fingerprint,
            reused = outcome.Build.Reused,
            dependencyGraph = outcome.Validation.DependencyGraph,
            diagnostics = outcome.Validation.Diagnostics
        });
        return 0;
    }

    private static void WriteProjectValidation(string command, ProjectValidationReport report) =>
        WriteJson(new
        {
            schemaVersion = 1,
            command,
            valid = report.Valid,
            projectRoot = report.ProjectRoot,
            resourceCount = report.ResourceCount,
            scriptArchiveCount = report.ScriptArchiveCount,
            diagnostics = report.Diagnostics,
            dependencyGraph = report.DependencyGraph
        });

    private static async Task<int> GameAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
            throw new CliInputException("Game command requires index or query.");

        return args[0] switch
        {
            "index" => await GameIndexAsync(args[1..], cancellationToken),
            "query" => await GameQueryAsync(args[1..], cancellationToken),
            "find" => await GameFindAsync(args[1..], cancellationToken),
            _ => throw new CliInputException($"Unknown game operation '{args[0]}'.")
        };
    }

    private static async Task<int> GameIndexAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, "--game-root", "--output");
        var gameRoot = RequireOption(options, "--game-root");
        var outputPath = RequireOption(options, "--output");
        var report = await GameCatalog.IndexAsync(gameRoot, outputPath, cancellationToken);

        WriteJson(new
        {
            schemaVersion = 1,
            command = "game.index",
            index = report.Index,
            gameRoot = report.GameRoot,
            gameVersion = report.GameVersion,
            packageCount = report.PackageCount,
            resourceCount = report.ResourceCount,
            fingerprint = report.Fingerprint,
            reused = report.Reused,
            precedenceVerified = false
        });
        return 0;
    }

    private static async Task<int> GameQueryAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, "--index", "--tgi");
        var indexPath = RequireOption(options, "--index");
        var resourceId = ResourceIdParser.ParseTgi(RequireOption(options, "--tgi"));
        var report = await GameCatalog.QueryAsync(indexPath, resourceId, cancellationToken);

        WriteJson(new
        {
            schemaVersion = 1,
            command = "game.query",
            index = report.Index,
            tgi = report.Tgi,
            resolution = report.Resolution,
            precedenceVerified = report.PrecedenceVerified,
            uniqueProvider = report.UniqueProvider,
            providers = report.Providers,
            note = report.Note
        });
        return 0;
    }

    private static async Task<int> GameFindAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, "--index", "--type", "--limit");
        var limit = options.TryGetValue("--limit", out var limitText)
            ? int.TryParse(limitText, out var parsedLimit)
                ? parsedLimit
                : throw new CliInputException("Game find limit must be a whole number.")
            : 20;
        var report = await GameCatalog.FindByTypeAsync(
            RequireOption(options, "--index"),
            ResourceIdParser.ParseResourceType(RequireOption(options, "--type")),
            limit,
            cancellationToken);

        WriteJson(new
        {
            schemaVersion = 1,
            command = "game.find",
            index = report.Index,
            type = $"0x{report.Type:X8}",
            matchCount = report.MatchCount,
            limit = report.Limit,
            truncated = report.Truncated,
            precedenceVerified = report.PrecedenceVerified,
            gameVersion = report.GameVersion,
            fingerprint = report.Fingerprint,
            packageCount = report.PackageCount,
            resourceCount = report.ResourceCount,
            generatedUtc = report.GeneratedUtc,
            resources = report.Resources
        });
        return 0;
    }

    private static async Task<int> CaspAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
            throw new CliInputException("CASP command requires inspect or rename.");

        switch (args[0])
        {
            case "inspect":
            {
                var options = ParseOptions(args[1..], "--package", "--tgi");
                var report = await Casp.InspectAsync(
                    RequireOption(options, "--package"),
                    ResourceIdParser.ParseTgi(RequireOption(options, "--tgi")),
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "casp.inspect",
                    package = report.Package,
                    tgi = report.Tgi,
                    version = report.Version,
                    name = report.Name,
                    nameByteLength = report.NameByteLength,
                    payloadBytes = report.PayloadBytes
                });
                return 0;
            }
            case "rename":
            {
                var options = ParseOptions(args[1..], "--package", "--tgi", "--name", "--output");
                var report = await Casp.RenameAsync(
                    RequireOption(options, "--package"),
                    ResourceIdParser.ParseTgi(RequireOption(options, "--tgi")),
                    RequireOption(options, "--name"),
                    RequireOption(options, "--output"),
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "casp.rename",
                    sourcePackage = report.SourcePackage,
                    outputPackage = report.OutputPackage,
                    tgi = report.Tgi,
                    version = report.Version,
                    previousName = report.PreviousName,
                    name = report.Name,
                    payloadBytes = report.PayloadBytes,
                    packageResourceCount = report.PackageResourceCount,
                    sha256 = report.Sha256,
                    verified = report.Verified
                });
                return 0;
            }
            default:
                throw new CliInputException($"Unknown CASP operation '{args[0]}'.");
        }
    }

    private static async Task<int> TDescAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
            throw new CliInputException("TDesc command requires refresh or inspect.");

        switch (args[0])
        {
            case "refresh":
            {
                var options = ParseOptions(args[1..], "--from-dir", "--game-version", "--source", "--output");
                var report = await TDescSnapshotWork.RefreshAsync(
                    RequireOption(options, "--from-dir"),
                    RequireOption(options, "--game-version"),
                    RequireOption(options, "--source"),
                    RequireOption(options, "--output"),
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "tdesc.refresh",
                    snapshot = report.Snapshot,
                    gameVersion = report.GameVersion,
                    source = report.Source,
                    sourceFileCount = report.SourceFileCount,
                    classCount = report.ClassCount,
                    sha256 = report.Sha256,
                    reused = report.Reused
                });
                return 0;
            }
            case "inspect":
            {
                var options = ParseOptions(args[1..], "--snapshot");
                var snapshotPath = RequireOption(options, "--snapshot");
                var snapshot = await TDescSnapshotWork.LoadAsync(snapshotPath, cancellationToken);
                await TDescSnapshotWork.VerifyDefinitionsAsync(snapshot, cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "tdesc.inspect",
                    snapshot = Path.GetFullPath(snapshotPath),
                    gameVersion = snapshot.Manifest.GameVersion,
                    source = snapshot.Manifest.Source,
                    importedUtc = snapshot.Manifest.ImportedUtc,
                    sourceFileCount = snapshot.Manifest.Files.Count,
                    classCount = snapshot.Index.Classes.Count,
                    sha256 = snapshot.Manifest.SnapshotSha256,
                    classes = snapshot.Index.Classes.Select(item => new
                    {
                        item.Module,
                        item.Class,
                        item.DefinitionKind,
                        item.InstanceType,
                        item.SourceFile,
                        fieldCount = item.Fields.Count,
                        fields = item.Fields.Select(field => new
                        {
                            field.Element,
                            field.Name,
                            field.Type,
                            field.Class
                        })
                    })
                });
                return 0;
            }
            default:
                throw new CliInputException($"Unknown TDesc operation '{args[0]}'.");
        }
    }

    private static async Task<int> StblAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
            throw new CliInputException("STBL command requires build, export, or import.");

        switch (args[0])
        {
            case "build":
            {
                var options = ParseOptions(args[1..], "--json", "--output");
                var report = await Stbl.BuildAsync(
                    RequireOption(options, "--json"),
                    RequireOption(options, "--output"),
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "stbl.build",
                    outputPackage = report.OutputPackage,
                    tgi = report.Tgi,
                    locale = report.Locale,
                    entryCount = report.EntryCount,
                    sha256 = report.Sha256,
                    verified = report.Verified
                });
                return 0;
            }
            case "export":
            {
                var options = ParseOptions(args[1..], "--package", "--tgi", "--output");
                var report = await Stbl.ExportAsync(
                    RequireOption(options, "--package"),
                    ResourceIdParser.ParseTgi(RequireOption(options, "--tgi")),
                    RequireOption(options, "--output"),
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "stbl.export",
                    package = report.Package,
                    output = report.Json,
                    tgi = report.Tgi,
                    locale = report.Locale,
                    entryCount = report.EntryCount
                });
                return 0;
            }
            case "import":
            {
                var options = ParseOptions(args[1..], "--package", "--json", "--output");
                var report = await Stbl.ImportAsync(
                    RequireOption(options, "--package"),
                    RequireOption(options, "--json"),
                    RequireOption(options, "--output"),
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "stbl.import",
                    sourcePackage = report.SourcePackage,
                    outputPackage = report.OutputPackage,
                    tgi = report.Tgi,
                    locale = report.Locale,
                    entryCount = report.EntryCount,
                    packageResourceCount = report.PackageResourceCount,
                    sha256 = report.Sha256,
                    verified = report.Verified
                });
                return 0;
            }
            default:
                throw new CliInputException($"Unknown STBL operation '{args[0]}'.");
        }
    }

    private static async Task<int> TuningAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
            throw new CliInputException("Tuning command requires build, validate, export, import, or extract.");

        switch (args[0])
        {
            case "extract":
            {
                var options = ParseOptions(args[1..], "--package", "--combined-tgi", "--name", "--output");
                var report = await CombinedTuning.ExtractAsync(
                    RequireOption(options, "--package"),
                    ResourceIdParser.ParseTgi(RequireOption(options, "--combined-tgi")),
                    RequireOption(options, "--name"),
                    RequireOption(options, "--output"),
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "tuning.extract",
                    package = report.Package,
                    output = report.Xml,
                    name = report.Name,
                    tuningClass = report.TuningClass,
                    module = report.Module,
                    tgi = report.Tgi,
                    extractedElementCount = report.ExtractedElementCount
                });
                return 0;
            }
            case "build":
            {
                var options = ParseOptions(args[1..], "--tgi", "--xml", "--output", "--snapshot", "--game-version", "--snapshot-sha256");
                var schema = await OpenTuningSchemaAsync(options, cancellationToken);
                var report = await Tuning.BuildAsync(
                    ResourceIdParser.ParseTgi(RequireOption(options, "--tgi")),
                    RequireOption(options, "--xml"),
                    RequireOption(options, "--output"),
                    cancellationToken,
                    schema);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "tuning.build",
                    outputPackage = report.OutputPackage,
                    tgi = report.Tgi,
                    rootName = report.RootName,
                    sha256 = report.Sha256,
                    verified = report.Verified,
                    schemaValidation = report.SchemaValidation
                });
                return 0;
            }
            case "validate":
            {
                var options = ParseOptions(args[1..], "--xml", "--tgi", "--snapshot", "--game-version", "--snapshot-sha256");
                var schema = await OpenTuningSchemaAsync(options, cancellationToken);
                var resourceId = options.TryGetValue("--tgi", out var tgi)
                    ? ResourceIdParser.ParseTgi(tgi)
                    : (ResourceId?)null;
                var report = await Tuning.ValidateAsync(
                    RequireOption(options, "--xml"), resourceId, cancellationToken, schema);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "tuning.validate",
                    xml = report.Xml,
                    tgi = report.Tgi,
                    wellFormed = report.WellFormed,
                    valid = report.Valid,
                    rootName = report.RootName,
                    tuningName = report.TuningName,
                    tuningClass = report.TuningClass,
                    semanticChecks = report.SemanticChecks,
                    nameHashes = report.NameHashes,
                    sharedIndexValidation = report.SharedIndexValidation is { } shared
                        ? new
                        {
                            status = shared.Status.ToString().ToLowerInvariant(),
                            sharedTableCount = shared.SharedTableCount,
                            definitionCount = shared.DefinitionCount,
                            referenceCount = shared.ReferenceCount,
                            issueCount = shared.IssueCount,
                            missingDefinitionIndexCount = shared.MissingDefinitionIndexCount,
                            invalidDefinitionIndexCount = shared.InvalidDefinitionIndexCount,
                            duplicateDefinitionIndexCount = shared.DuplicateDefinitionIndexCount,
                            missingReferenceIndexCount = shared.MissingReferenceIndexCount,
                            invalidReferenceIndexCount = shared.InvalidReferenceIndexCount,
                            unresolvedReferenceCount = shared.UnresolvedReferenceCount,
                            ambiguousReferenceCount = shared.AmbiguousReferenceCount,
                            unsupportedReasonCount = shared.UnsupportedReasonCount,
                            examples = shared.Examples
                        }
                        : null,
                    schemaValidation = report.SchemaValidation,
                    note = report.Note
                });
                return report.Valid ? 0 : 1;
            }
            case "export":
            {
                var options = ParseOptions(args[1..], "--package", "--tgi", "--output");
                var report = await Tuning.ExportAsync(
                    RequireOption(options, "--package"),
                    ResourceIdParser.ParseTgi(RequireOption(options, "--tgi")),
                    RequireOption(options, "--output"),
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "tuning.export",
                    package = report.Package,
                    output = report.Xml,
                    tgi = report.Tgi,
                    rootName = report.RootName,
                    tuningName = report.TuningName,
                    tuningClass = report.TuningClass
                });
                return 0;
            }
            case "import":
            {
                var options = ParseOptions(args[1..], "--package", "--tgi", "--xml", "--output", "--snapshot", "--game-version", "--snapshot-sha256");
                var schema = await OpenTuningSchemaAsync(options, cancellationToken);
                var report = await Tuning.ImportAsync(
                    RequireOption(options, "--package"),
                    ResourceIdParser.ParseTgi(RequireOption(options, "--tgi")),
                    RequireOption(options, "--xml"),
                    RequireOption(options, "--output"),
                    cancellationToken,
                    schema);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "tuning.import",
                    sourcePackage = report.SourcePackage,
                    outputPackage = report.OutputPackage,
                    tgi = report.Tgi,
                    rootName = report.RootName,
                    packageResourceCount = report.PackageResourceCount,
                    sha256 = report.Sha256,
                    verified = report.Verified,
                    schemaValidation = report.SchemaValidation
                });
                return 0;
            }
            default:
                throw new CliInputException($"Unknown tuning operation '{args[0]}'.");
        }
    }

    private static async Task<int> SimDataAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
            throw new CliInputException("SimData command requires inspect, roundtrip, or set.");

        switch (args[0])
        {
            case "inspect":
            {
                var options = ParseOptions(args[1..], "--package", "--tgi");
                var report = await SimData.InspectAsync(
                    RequireOption(options, "--package"),
                    ResourceIdParser.ParseTgi(RequireOption(options, "--tgi")),
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "simdata.inspect",
                    package = report.Package,
                    tgi = report.Tgi,
                    resourceName = report.ResourceName,
                    version = report.Version,
                    tableCount = report.TableCount,
                    rowCount = report.RowCount,
                    tables = report.Tables
                });
                return 0;
            }
            case "roundtrip":
            {
                var options = ParseOptions(args[1..], "--package", "--tgi", "--output");
                var report = await SimData.RoundtripAsync(
                    RequireOption(options, "--package"),
                    ResourceIdParser.ParseTgi(RequireOption(options, "--tgi")),
                    RequireOption(options, "--output"),
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "simdata.roundtrip",
                    sourcePackage = report.SourcePackage,
                    outputPackage = report.OutputPackage,
                    tgi = report.Tgi,
                    resourceName = report.ResourceName,
                    version = report.Version,
                    tableCount = report.TableCount,
                    rowCount = report.RowCount,
                    sha256 = report.Sha256,
                    verified = report.Verified
                });
                return 0;
            }
            case "set":
            {
                var options = ParseOptions(args[1..], "--package", "--tgi", "--table", "--row", "--column", "--value", "--output");
                var rowText = RequireOption(options, "--row");
                if (!int.TryParse(rowText, NumberStyles.None, CultureInfo.InvariantCulture, out var row))
                    throw new CliInputException($"SimData row '{rowText}' must be a non-negative integer.");

                var report = await SimData.SetScalarAsync(
                    RequireOption(options, "--package"),
                    ResourceIdParser.ParseTgi(RequireOption(options, "--tgi")),
                    RequireOption(options, "--table"),
                    row,
                    RequireOption(options, "--column"),
                    RequireOption(options, "--value"),
                    RequireOption(options, "--output"),
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "simdata.set",
                    sourcePackage = report.SourcePackage,
                    outputPackage = report.OutputPackage,
                    tgi = report.Tgi,
                    resourceName = report.ResourceName,
                    version = report.Version,
                    table = report.Table,
                    row = report.Row,
                    column = report.Column,
                    type = report.Type,
                    oldValue = report.OldValue,
                    newValue = report.NewValue,
                    packageResourceCount = report.PackageResourceCount,
                    sha256 = report.Sha256,
                    verified = report.Verified
                });
                return 0;
            }
            default:
                throw new CliInputException($"Unknown SimData operation '{args[0]}'.");
        }
    }

    private static async Task<int> ImageAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
            throw new CliInputException("Image command requires export, build, or import.");

        switch (args[0])
        {
            case "export":
            {
                var options = ParseOptions(args[1..], "--package", "--tgi", "--output", "--bundle", "--mip");
                if (options.ContainsKey("--output") == options.ContainsKey("--bundle"))
                    throw new CliInputException("Image export requires exactly one of '--output' or '--bundle'.");
                int? mipLevel = null;
                if (options.TryGetValue("--mip", out var mipText))
                {
                    if (!int.TryParse(mipText, out var parsedMip) || parsedMip < 0)
                        throw new CliInputException("Image mip level must be a non-negative integer.");
                    mipLevel = parsedMip;
                }
                var packagePath = RequireOption(options, "--package");
                var resourceId = ResourceIdParser.ParseTgi(RequireOption(options, "--tgi"));
                if (options.TryGetValue("--bundle", out var bundlePath))
                {
                    if (resourceId.Type != RleTextureCodec.RlesResourceTypeId)
                        throw new CliInputException("The '--bundle' option is available only for RLES image export.");

                    var report = await Image.ExportRlesAsync(
                        packagePath,
                        resourceId,
                        bundlePath,
                        cancellationToken,
                        mipLevel);
                    WriteJson(new
                    {
                        schemaVersion = 1,
                        command = "image.export",
                        package = report.Package,
                        codec = "rles",
                        bundle = report.Bundle,
                        colorPng = report.ColorPng,
                        maskPng = report.MaskPng,
                        tgi = report.Tgi,
                        sourceWidth = report.SourceWidth,
                        sourceHeight = report.SourceHeight,
                        width = report.Width,
                        height = report.Height,
                        mipLevel = report.MipLevel,
                        mipCount = report.MipCount,
                        colorPngBytes = report.ColorPngBytes,
                        maskPngBytes = report.MaskPngBytes
                    });
                    return 0;
                }

                if (resourceId.Type == RleTextureCodec.RlesResourceTypeId)
                    throw new CliInputException("RLES image export requires '--bundle' to preserve both color and mask planes.");

                var singlePlaneReport = await Image.ExportAsync(
                    packagePath,
                    resourceId,
                    RequireOption(options, "--output"),
                    cancellationToken,
                    mipLevel);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "image.export",
                    package = singlePlaneReport.Package,
                    output = singlePlaneReport.Png,
                    tgi = singlePlaneReport.Tgi,
                    codec = singlePlaneReport.Codec,
                    width = singlePlaneReport.Width,
                    height = singlePlaneReport.Height,
                    pngBytes = singlePlaneReport.PngBytes,
                    mipLevel = singlePlaneReport.MipLevel,
                    mipCount = singlePlaneReport.MipCount
                });
                return 0;
            }
            case "build":
            {
                var options = ParseOptions(args[1..], "--tgi", "--png", "--bundle", "--output");
                if (options.ContainsKey("--png") == options.ContainsKey("--bundle"))
                    throw new CliInputException("Image build requires exactly one of '--png' or '--bundle'.");
                var resourceId = ResourceIdParser.ParseTgi(RequireOption(options, "--tgi"));
                var outputPath = RequireOption(options, "--output");
                if (options.TryGetValue("--bundle", out var bundlePath))
                {
                    if (resourceId.Type != RleTextureCodec.RlesResourceTypeId)
                        throw new CliInputException("The '--bundle' option is available only for RLES image build/import.");

                    var rlesReport = await Image.BuildRlesAsync(resourceId, bundlePath, outputPath, cancellationToken);
                    WriteJson(new
                    {
                        schemaVersion = 1,
                        command = "image.build",
                        outputPackage = rlesReport.OutputPackage,
                        bundle = rlesReport.Bundle,
                        tgi = rlesReport.Tgi,
                        width = rlesReport.Width,
                        height = rlesReport.Height,
                        mipCount = rlesReport.MipCount,
                        pixelVerifiedMipCount = rlesReport.PixelVerifiedMipCount,
                        allMipsDecoded = rlesReport.AllMipsDecoded,
                        color = rlesReport.Color,
                        mask = rlesReport.Mask,
                        sha256 = rlesReport.Sha256,
                        readBackVerified = rlesReport.ReadBackVerified
                    });
                    return 0;
                }

                if (resourceId.Type == RleTextureCodec.RlesResourceTypeId)
                    throw new CliInputException("RLES image build requires '--bundle' with both color and mask planes.");

                var report = await Image.BuildAsync(
                    resourceId,
                    RequireOption(options, "--png"),
                    outputPath,
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "image.build",
                    outputPackage = report.OutputPackage,
                    tgi = report.Tgi,
                    width = report.Width,
                    height = report.Height,
                    identicalPixels = report.IdenticalPixels,
                    totalPixels = report.TotalPixels,
                    maximumChannelDelta = report.MaximumChannelDelta,
                    meanChannelDelta = report.MeanChannelDelta,
                    sha256 = report.Sha256,
                    readBackVerified = report.ReadBackVerified
                });
                return 0;
            }
            case "import":
            {
                var options = ParseOptions(args[1..], "--package", "--tgi", "--png", "--bundle", "--output");
                if (options.ContainsKey("--png") == options.ContainsKey("--bundle"))
                    throw new CliInputException("Image import requires exactly one of '--png' or '--bundle'.");
                var packagePath = RequireOption(options, "--package");
                var resourceId = ResourceIdParser.ParseTgi(RequireOption(options, "--tgi"));
                var outputPath = RequireOption(options, "--output");
                if (options.TryGetValue("--bundle", out var bundlePath))
                {
                    if (resourceId.Type != RleTextureCodec.RlesResourceTypeId)
                        throw new CliInputException("The '--bundle' option is available only for RLES image build/import.");

                    var rlesReport = await Image.ImportRlesAsync(
                        packagePath,
                        resourceId,
                        bundlePath,
                        outputPath,
                        cancellationToken);
                    WriteJson(new
                    {
                        schemaVersion = 1,
                        command = "image.import",
                        sourcePackage = rlesReport.SourcePackage,
                        outputPackage = rlesReport.OutputPackage,
                        bundle = rlesReport.Bundle,
                        tgi = rlesReport.Tgi,
                        width = rlesReport.Width,
                        height = rlesReport.Height,
                        packageResourceCount = rlesReport.PackageResourceCount,
                        mipCount = rlesReport.MipCount,
                        pixelVerifiedMipCount = rlesReport.PixelVerifiedMipCount,
                        allMipsDecoded = rlesReport.AllMipsDecoded,
                        color = rlesReport.Color,
                        mask = rlesReport.Mask,
                        sha256 = rlesReport.Sha256,
                        readBackVerified = rlesReport.ReadBackVerified
                    });
                    return 0;
                }

                if (resourceId.Type == RleTextureCodec.RlesResourceTypeId)
                    throw new CliInputException("RLES image import requires '--bundle' with both color and mask planes.");

                var report = await Image.ImportAsync(
                    packagePath,
                    resourceId,
                    RequireOption(options, "--png"),
                    outputPath,
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "image.import",
                    sourcePackage = report.SourcePackage,
                    outputPackage = report.OutputPackage,
                    tgi = report.Tgi,
                    width = report.Width,
                    height = report.Height,
                    packageResourceCount = report.PackageResourceCount,
                    identicalPixels = report.IdenticalPixels,
                    totalPixels = report.TotalPixels,
                    maximumChannelDelta = report.MaximumChannelDelta,
                    meanChannelDelta = report.MeanChannelDelta,
                    sha256 = report.Sha256,
                    readBackVerified = report.ReadBackVerified
                });
                return 0;
            }
            default:
                throw new CliInputException($"Unknown image operation '{args[0]}'.");
        }
    }

    private static async Task<int> ConflictAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
            throw new CliInputException("Conflict command requires game or packages.");

        switch (args[0])
        {
            case "game":
            {
                var options = ParseOptions(args[1..], "--package", "--index");
                var report = await Conflicts.CompareWithGameAsync(
                    RequireOption(options, "--package"),
                    RequireOption(options, "--index"),
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "conflict.game",
                    package = report.Package,
                    index = report.Index,
                    packageResourceCount = report.PackageResourceCount,
                    conflictingResourceCount = report.ConflictingResourceCount,
                    precedenceVerified = report.PrecedenceVerified,
                    conflicts = report.Conflicts
                });
                return 0;
            }
            case "packages":
            {
                var options = ParseOptions(args[1..], "--left", "--right");
                var report = await Conflicts.ComparePackagesAsync(
                    RequireOption(options, "--left"),
                    RequireOption(options, "--right"),
                    cancellationToken);
                WriteJson(new
                {
                    schemaVersion = 1,
                    command = "conflict.packages",
                    leftPackage = report.LeftPackage,
                    rightPackage = report.RightPackage,
                    leftResourceCount = report.LeftResourceCount,
                    rightResourceCount = report.RightResourceCount,
                    sharedResourceCount = report.SharedTgis.Count,
                    sharedTgis = report.SharedTgis
                });
                return 0;
            }
            default:
                throw new CliInputException($"Unknown conflict operation '{args[0]}'.");
        }
    }

    private static async Task<int> InspectAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = ParseOptions(args, "--package");
        var packagePath = RequireOption(options, "--package");
        var report = await PackageWork.InspectAsync(packagePath, cancellationToken);

        WriteJson(new
        {
            schemaVersion = 1,
            command = "inspect",
            package = report.Package,
            resourceCount = report.Resources.Count,
            resources = report.Resources.Select(resource => new
            {
                type = $"0x{resource.Id.Type:X8}",
                group = $"0x{resource.Id.Group:X8}",
                instance = $"0x{resource.Id.Instance:X16}",
                sizeBytes = resource.SizeBytes
            })
        });
        return 0;
    }

    private static async Task<TuningSchemaValidator?> OpenTuningSchemaAsync(
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        var names = new[] { "--snapshot", "--game-version", "--snapshot-sha256" };
        var present = names.Where(options.ContainsKey).ToArray();
        if (present.Length == 0)
            return null;
        if (present.Length != names.Length)
            throw new CliInputException("Pinned schema validation requires --snapshot, --game-version, and --snapshot-sha256 together.");
        return await TuningSchemaValidator.OpenAsync(new TuningSchemaPin(
            RequireOption(options, "--snapshot"),
            RequireOption(options, "--game-version"),
            RequireOption(options, "--snapshot-sha256")), cancellationToken);
    }

    private static Dictionary<string, string> ParseOptions(string[] args, params string[] allowedOptions)
    {
        var allowed = allowedOptions.ToHashSet(StringComparer.Ordinal);
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var index = 0; index < args.Length; index += 2)
        {
            var option = args[index];
            if (!allowed.Contains(option))
                throw new CliInputException($"Unknown option '{option}'.");
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new CliInputException($"Option '{option}' requires a value.");
            if (!parsed.TryAdd(option, args[index + 1]))
                throw new CliInputException($"Option '{option}' was provided more than once.");
        }

        return parsed;
    }

    private static string RequireOption(IReadOnlyDictionary<string, string> options, string name)
    {
        if (options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        throw new CliInputException($"Required option '{name}' was not provided.");
    }

    private static void WriteJson<T>(T value) =>
        Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static void WriteError(string code, string message) =>
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            error = new { code, message }
        }, JsonOptions));

    private const string HelpText = """
        S4MT — narzędzia do budowania i inspekcji pakietów The Sims 4

        Użycie:
          dotnet run --project src/S4MT.Cli -- build --manifest <plik.json> --output <plik.package>
          dotnet run --project src/S4MT.Cli -- package merge --manifest <merge.json> --output <wynik.package>
          dotnet run --project src/S4MT.Cli -- resource export --package <plik.package> --tgi <type:group:instance> --output <payload.bin>
          dotnet run --project src/S4MT.Cli -- resource import --package <plik.package> --tgi <type:group:instance> --payload <payload.bin> --output <wynik.package>
          dotnet run --project src/S4MT.Cli -- inspect --package <plik.package>
          dotnet run --project src/S4MT.Cli -- verify --manifest <plik.json> --package <plik.package>
          dotnet run --project src/S4MT.Cli -- project init --name <Nazwa> --path <katalog>
          dotnet run --project src/S4MT.Cli -- project validate --project <katalog>
          dotnet run --project src/S4MT.Cli -- project build --project <katalog> --output-root <katalog>
          dotnet run --project src/S4MT.Cli -- game index --game-root <katalog-gry> --output <index.jsonl.gz>
          dotnet run --project src/S4MT.Cli -- game query --index <index.jsonl.gz> --tgi <type:group:instance>
          dotnet run --project src/S4MT.Cli -- game find --index <index.jsonl.gz> --type <type> [--limit <1-1000>]
          dotnet run --project src/S4MT.Cli -- tdesc refresh --from-dir <tdesc-katalog> --game-version <x.y.z.build> --source <źródło> --output <snapshot>
          dotnet run --project src/S4MT.Cli -- tdesc inspect --snapshot <snapshot>
          dotnet run --project src/S4MT.Cli -- stbl export --package <plik.package> --tgi <type:group:instance> --output <tabela.json>
          dotnet run --project src/S4MT.Cli -- stbl import --package <plik.package> --json <tabela.json> --output <wynik.package>
          dotnet run --project src/S4MT.Cli -- stbl build --json <tabela.json> --output <nowy.package>
          dotnet run --project src/S4MT.Cli -- casp inspect --package <plik.package> --tgi <type:group:instance>
          dotnet run --project src/S4MT.Cli -- casp rename --package <plik.package> --tgi <type:group:instance> --name <nowa-nazwa> --output <wynik.package>
          dotnet run --project src/S4MT.Cli -- tuning validate --xml <tuning.xml> [--tgi <type:group:instance>] [--snapshot <snapshot> --game-version <x.y.z.build> --snapshot-sha256 <sha256>]
          dotnet run --project src/S4MT.Cli -- tuning extract --package <gra.package> --combined-tgi <TGI-CombinedTuning> --name <nazwa> --output <tuning.xml>
          dotnet run --project src/S4MT.Cli -- tuning export --package <plik.package> --tgi <type:group:instance> --output <tuning.xml>
          dotnet run --project src/S4MT.Cli -- tuning import --package <plik.package> --tgi <type:group:instance> --xml <tuning.xml> --output <wynik.package>
          dotnet run --project src/S4MT.Cli -- tuning build --tgi <type:group:instance> --xml <tuning.xml> --output <nowy.package>
          dotnet run --project src/S4MT.Cli -- simdata inspect --package <plik.package> --tgi <type:group:instance>
          dotnet run --project src/S4MT.Cli -- simdata roundtrip --package <plik.package> --tgi <type:group:instance> --output <wynik.package>
          dotnet run --project src/S4MT.Cli -- simdata set --package <plik.package> --tgi <type:group:instance> --table <nazwa> --row <indeks> --column <nazwa> --value <wartość> --output <wynik.package>
          dotnet run --project src/S4MT.Cli -- image export --package <plik.package> --tgi <type:group:instance> --output <obraz.png> [--mip <indeks>]
          dotnet run --project src/S4MT.Cli -- image build --tgi <type:group:instance> --png <obraz.png> --output <nowy.package>
          dotnet run --project src/S4MT.Cli -- image import --package <plik.package> --tgi <type:group:instance> --png <obraz.png> --output <wynik.package>
          dotnet run --project src/S4MT.Cli -- image export --package <plik.package> --tgi <TGI-RLES> --bundle <katalog> [--mip <indeks>]
          dotnet run --project src/S4MT.Cli -- image build --tgi <TGI-RLES> --bundle <katalog> --output <nowy.package>
          dotnet run --project src/S4MT.Cli -- image import --package <plik.package> --tgi <TGI-RLES> --bundle <katalog> --output <wynik.package>
          dotnet run --project src/S4MT.Cli -- conflict game --package <mod.package> --index <index.jsonl.gz>
          dotnet run --project src/S4MT.Cli -- conflict packages --left <a.package> --right <b.package>
          dotnet run --project src/S4MT.Cli -- --version

        Build przyjmuje manifest formatu 1. Ścieżki źródeł są liczone od katalogu manifestu.
        Wynik i błędy są zwracane jako JSON; błędy trafiają na stderr.
        """;
}
