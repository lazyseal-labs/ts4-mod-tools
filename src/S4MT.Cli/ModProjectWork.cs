using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LlamaLogic.Packages;

namespace S4MT.Cli;

internal sealed class ModProjectWork
{
    private const string ProjectFileName = "s4mt.json";
    private const string DefaultPackageManifestName = "package.json";
    private const string BuildMarkerName = "s4mt-build.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    private readonly PackageWork packageWork = new();
    private readonly StblWork stblWork = new();
    private readonly TuningWork tuningWork = new();
    private readonly SimDataWork simDataWork = new();
    private readonly ImageWork imageWork = new();
    private readonly GameCatalogWork gameCatalogWork = new();

    public ProjectInitializationReport Initialize(string projectPath, string name)
    {
        ValidateProjectName(name);

        var projectRoot = Path.GetFullPath(projectPath);
        if (File.Exists(projectRoot))
            throw new CliInputException($"Project path is a file: {projectRoot}");

        Directory.CreateDirectory(projectRoot);
        var projectFile = Path.Combine(projectRoot, ProjectFileName);
        var created = !File.Exists(projectFile);
        ProjectDocument document;

        if (created)
        {
            document = new ProjectDocument
            {
                Format = 1,
                Name = name,
                PackageManifest = DefaultPackageManifestName,
                ScriptArchives = []
            };
            WriteIfAbsent(projectFile, JsonSerializer.Serialize(document, JsonOptions));
        }
        else
        {
            document = ReadProjectDocument(projectFile);
            if (document.Format != 1)
                throw new CliInputException($"Unsupported project format {document.Format}; expected 1.");
            ValidateProjectName(document.Name);
            if (!string.Equals(document.Name, name, StringComparison.Ordinal))
                throw new CliInputException(
                    $"Project already exists with name '{document.Name}', not '{name}'.");
        }

        var packageManifest = ResolveContainedPath(projectRoot, document.PackageManifest, "package manifest");
        Directory.CreateDirectory(Path.GetDirectoryName(packageManifest)!);
        WriteIfAbsent(packageManifest, """
            {
              "format": 1,
              "resources": []
            }
            """);
        Directory.CreateDirectory(Path.Combine(projectRoot, "resources"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "scripts"));

        return new ProjectInitializationReport(projectFile, packageManifest, created);
    }

    public async Task<ProjectValidationReport> ValidateAsync(
        string projectPath,
        CancellationToken cancellationToken,
        string? gameIndexPath = null) =>
        (await PrepareAsync(projectPath, cancellationToken, gameIndexPath)).Validation;

    private async Task<PreparedProject> PrepareAsync(
        string projectPath,
        CancellationToken cancellationToken,
        string? gameIndexPath)
    {
        LoadedProject project;
        try
        {
            project = LoadProject(projectPath);
        }
        catch (CliInputException exception)
        {
            return InvalidPreparedProject(
                Path.GetFullPath(projectPath),
                new ProjectDiagnostic("error", "invalid_project", exception.Message, projectPath));
        }

        if (!File.Exists(project.PackageManifestPath))
            return InvalidPreparedProject(
                project.Root,
                new ProjectDiagnostic(
                    "error", "package_manifest_missing", "Package manifest does not exist.", project.PackageManifestPath));

        BuildSpec spec;
        try
        {
            spec = await ManifestReader.ReadAsync(
                project.PackageManifestPath,
                cancellationToken,
                allowEmptyResources: true,
                requireContainedSources: true);
        }
        catch (CliInputException exception)
        {
            return InvalidPreparedProject(
                project.Root,
                new ProjectDiagnostic("error", "invalid_package_manifest", exception.Message, project.PackageManifestPath));
        }

        var diagnostics = new List<ProjectDiagnostic>();
        var observations = new List<(ResourceId Source, TuningReferenceObservation Reference, string Evidence, string Requirement, string? Reason)>();
        var scans = new List<ResourceScanReport>();
        var packageIds = spec.Resources.Select(resource => resource.Id).ToHashSet();
        foreach (var dependency in project.Dependencies)
        {
            if (!packageIds.Contains(dependency.Source))
            {
                diagnostics.Add(new ProjectDiagnostic("error", "dependency_source_missing",
                    $"Dependency source {dependency.Source} is not declared in the package manifest.", project.PackageManifestPath));
                continue;
            }
            if (dependency.Requirement == "bundled" && !packageIds.Contains(dependency.Target))
                diagnostics.Add(new ProjectDiagnostic("error", "bundled_dependency_missing",
                    $"Bundled dependency target {dependency.Target} is not present in this project's package.", project.ProjectFilePath));
            observations.Add((dependency.Source,
                new TuningReferenceObservation("manifest", dependency.Target.ToString(), dependency.Target, "resource_key"),
                "manifest", dependency.Requirement, dependency.Reason));
        }
        TuningSchemaValidator? schemaValidator = null;
        if (project.TuningSchema is { } schemaPin)
        {
            try
            {
                schemaValidator = await TuningSchemaValidator.OpenAsync(schemaPin, cancellationToken);
            }
            catch (CliInputException exception)
            {
                return InvalidPreparedProject(project.Root,
                    new ProjectDiagnostic("error", "invalid_tuning_schema_pin", exception.Message, project.TuningSchemaPath));
            }
        }
        foreach (var resource in spec.Resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = ClassifyProjectResource(resource.Id);
            if (kind != ProjectResourceKind.TuningXml || resource.Encoding != ResourceEncoding.Utf8)
                scans.Add(new ResourceScanReport(resource.Id.ToString(), "not_scanned",
                    ["No reference extractor is available for this resource encoding or family."]));
            switch (resource.Encoding)
            {
                case ResourceEncoding.Utf8:
                {
                    switch (kind)
                    {
                        case ProjectResourceKind.TuningXml:
                            try
                            {
                                var validation = tuningWork.ValidatePayload(resource.Id, resource.Content);
                                var schemaAnalysis = await tuningWork.AnalyzeSchemaPayloadAsync(
                                    resource.Id, resource.Content, schemaValidator, cancellationToken);
                                foreach (var reference in schemaAnalysis.References)
                                    observations.Add((resource.Id, reference, "tdesc", "observed", null));
                                scans.Add(new ResourceScanReport(resource.Id.ToString(),
                                    schemaValidator is null ? "not_scanned" : "partial",
                                    schemaValidator is null
                                        ? ["No pinned TDESC schema was provided."]
                                        : ["Only supported top-level TDESC reference fields are scanned."]));
                                var schemaValidation = schemaAnalysis.Validation;
                                if (schemaValidation is not null)
                                {
                                    foreach (var item in schemaValidation.Diagnostics)
                                        diagnostics.Add(new ProjectDiagnostic(item.Severity, item.Code, item.Message, resource.SourcePath));
                                }
                                if (validation is { Valid: false })
                                {
                                    var code = validation.Status is CombinedTuningSharedIndexStatus.Unsupported
                                        ? "unsupported_combined_tuning_shared_index"
                                        : "invalid_combined_tuning_shared_index";
                                    diagnostics.Add(new ProjectDiagnostic(
                                        "error",
                                        code,
                                        TuningWork.DescribeSharedIndexValidationFailure(resource.Id, validation),
                                        resource.SourcePath));
                                }
                            }
                            catch (CliInputException exception)
                            {
                                diagnostics.Add(new ProjectDiagnostic(
                                    "error", "invalid_tuning_xml", exception.Message, resource.SourcePath));
                            }
                            break;
                        case ProjectResourceKind.SimData:
                        case ProjectResourceKind.Image:
                        case ProjectResourceKind.StringTable:
                            diagnostics.Add(new ProjectDiagnostic(
                                "error",
                                "invalid_resource_encoding",
                                $"Resource {resource.Id} must use the 'bytes' encoding.",
                                resource.SourcePath));
                            break;
                        case ProjectResourceKind.Opaque:
                            break;
                    }
                    break;
                }
                case ResourceEncoding.Bytes:
                {
                    switch (kind)
                    {
                        case ProjectResourceKind.SimData:
                        {
                            var warning = simDataWork.ValidatePayload(resource.Id, resource.Content);
                            if (warning is not null)
                                diagnostics.Add(new ProjectDiagnostic(
                                    "warning", "simdata_validation_unavailable", warning, resource.SourcePath));
                            break;
                        }
                        case ProjectResourceKind.Image:
                        {
                            var diagnosticCode = ImageWork.ProjectValidationDiagnosticCode(resource.Id);
                            try
                            {
                                await imageWork.ValidateProjectPayloadAsync(resource.Id, resource.Content, cancellationToken);
                            }
                            catch (CliInputException exception)
                            {
                                diagnostics.Add(new ProjectDiagnostic(
                                    "error", diagnosticCode, exception.Message, resource.SourcePath));
                            }
                            break;
                        }
                        case ProjectResourceKind.StringTable:
                        {
                            try
                            {
                                stblWork.ValidatePayload(resource.Id, resource.Content);
                            }
                            catch (CliInputException exception)
                            {
                                diagnostics.Add(new ProjectDiagnostic(
                                    "error", "invalid_stbl", exception.Message, resource.SourcePath));
                            }
                            break;
                        }
                        case ProjectResourceKind.Opaque:
                            break;
                        case ProjectResourceKind.TuningXml:
                            if (schemaValidator is not null)
                                diagnostics.Add(new ProjectDiagnostic("warning", "tuning_schema_unverified",
                                    "This binary tuning resource cannot be checked against the pinned XML schema.", resource.SourcePath));
                            break;
                    }
                    break;
                }
            }
        }

        var scripts = new List<ScriptArtifact>(project.ScriptArchivePaths.Count);
        foreach (var scriptPath in project.ScriptArchivePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(scriptPath))
            {
                diagnostics.Add(new ProjectDiagnostic(
                    "error", "script_archive_missing", "Script archive does not exist.", scriptPath));
                continue;
            }

            try
            {
                var content = await File.ReadAllBytesAsync(scriptPath, cancellationToken);
                ValidateScriptArchive(content);
                var relativePath = Path.GetRelativePath(project.Root, scriptPath)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                scripts.Add(new ScriptArtifact(relativePath, scriptPath, content));
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(new ProjectDiagnostic(
                    "error", "invalid_script_archive", exception.Message, scriptPath));
            }
            catch (IOException exception)
            {
                diagnostics.Add(new ProjectDiagnostic(
                    "error", "script_archive_io", exception.Message, scriptPath));
            }
        }

        if (spec.Resources.Count == 0 && scripts.Count == 0)
            diagnostics.Add(new ProjectDiagnostic(
                "warning", "empty_project", "Project has no package resources or script archives.", project.Root));

        GameProviderLookupReport? catalog = null;
        if (!string.IsNullOrWhiteSpace(gameIndexPath))
        {
            var exactTargets = observations.Where(item => item.Reference.Target.HasValue)
                .Select(item => item.Reference.Target!.Value).ToArray();
            try
            {
                catalog = await gameCatalogWork.LookupProvidersAsync(
                    gameIndexPath,
                    exactTargets.Select(target => target.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    cancellationToken,
                    exactTargets.Select(target => target.Type).ToHashSet());
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                diagnostics.Add(new ProjectDiagnostic("error", "invalid_game_index", exception.Message, gameIndexPath));
            }
        }

        var graphObservations = observations.Select(item =>
        {
            var target = item.Reference.Target;
            IReadOnlyList<GameIndexProvider> providers = target is { } id && catalog is not null &&
                catalog.ProvidersByTgi.TryGetValue(id.ToString(), out var found) ? found : [];
            var resolution = target is null ? "logical_only" : packageIds.Contains(target.Value)
                ? "in_project"
                : catalog is null ? "catalog_unavailable"
                : providers.Count > 0 ? providers.Count == 1 ? "game_index_present" : "game_index_multiple_providers"
                : catalog.ObservedTypes.Contains(target.Value.Type) ? "indexed_type_no_exact_match" : "type_unseen_in_index";
            return new DependencyObservationReport(item.Source.ToString(),
                target is null ? "logical" : "resource_tgi", target?.ToString(),
                target is null ? item.Reference.Domain : null, item.Reference.RawValue,
                item.Evidence == "manifest" ? "manifest_assertion" :
                    target is null ? "tdesc_logical_value" : "tdesc_resource_key",
                item.Reference.Field, resolution, providers, item.Requirement, item.Reason);
        }).OrderBy(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Location, StringComparer.Ordinal)
            .ThenBy(item => item.Target?.ToString(), StringComparer.Ordinal).ToArray();
        var graph = schemaValidator is null && graphObservations.Length == 0 && catalog is null
            ? null
            : new DependencyGraphReport(scans.OrderBy(scan => scan.Source, StringComparer.Ordinal).ToArray(),
                graphObservations, catalog?.Index, catalog?.Fingerprint, catalog?.PrecedenceVerified);

        var report = new ProjectValidationReport(
            diagnostics.All(diagnostic => diagnostic.Severity != "error"),
            project.Root,
            spec.Resources.Count,
            scripts.Count,
            diagnostics,
            graph);
        return new PreparedProject(project, spec, scripts, report);
    }

    private static ProjectResourceKind ClassifyProjectResource(ResourceId id)
    {
        if (TuningWork.IsTuningType(id))
            return ProjectResourceKind.TuningXml;
        if (id.Type == unchecked((uint)ResourceType.SimData))
            return ProjectResourceKind.SimData;
        if (ImageWork.SupportsProjectValidation(id))
            return ProjectResourceKind.Image;
        if (StblWork.IsStringTable(id))
            return ProjectResourceKind.StringTable;
        return ProjectResourceKind.Opaque;
    }

    public async Task<ProjectBuildOutcome> BuildAsync(
        string projectPath,
        string outputRootPath,
        CancellationToken cancellationToken,
        string? gameIndexPath = null)
    {
        var prepared = await PrepareAsync(projectPath, cancellationToken, gameIndexPath);
        var validation = prepared.Validation;
        if (!validation.Valid)
            return new ProjectBuildOutcome(validation, null);

        var project = prepared.Project!;
        var spec = prepared.Spec!;
        var scripts = prepared.Scripts;

        if (spec.Resources.Count == 0 && scripts.Count == 0)
        {
            var emptyDiagnostic = new ProjectDiagnostic(
                "error", "nothing_to_build", "Add a package resource or a .ts4script archive before building.", project.Root);
            return new ProjectBuildOutcome(
                validation with { Valid = false, Diagnostics = [.. validation.Diagnostics, emptyDiagnostic] },
                null);
        }

        var fingerprint = ComputeFingerprint(project.Name, spec, scripts);
        var outputRoot = Path.GetFullPath(outputRootPath);
        Directory.CreateDirectory(outputRoot);
        var outputDirectory = Path.Combine(outputRoot, $"{project.Name}-{fingerprint[..16]}");
        if (Directory.Exists(outputDirectory))
        {
            if (!await ExistingBuildMatchesAsync(
                    outputDirectory, project.Name, fingerprint, spec, scripts, cancellationToken))
                throw new CliInputException(
                    $"Build output exists but does not match its fingerprint; it was left unchanged: {outputDirectory}");

            return new ProjectBuildOutcome(validation, MakeBuildReport(
                outputDirectory, project.Name, fingerprint, spec, scripts, reused: true));
        }

        var stagingDirectory = Path.Combine(outputRoot, $".s4mt-stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            string? packagePath = null;
            if (spec.Resources.Count > 0)
            {
                packagePath = Path.Combine(stagingDirectory, $"{project.Name}.package");
                await packageWork.BuildAsync(spec, packagePath, cancellationToken);
            }

            foreach (var script in scripts)
            {
                var destination = Path.Combine(
                    stagingDirectory,
                    script.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await File.WriteAllBytesAsync(destination, script.Content, cancellationToken);
            }

            var marker = new BuildMarker
            {
                Format = 1,
                ProjectName = project.Name,
                Fingerprint = fingerprint,
                PackageFile = packagePath is null ? null : Path.GetFileName(packagePath),
                ScriptFiles = scripts.Select(script => script.RelativePath).ToList()
            };
            var markerPath = Path.Combine(stagingDirectory, BuildMarkerName);
            await File.WriteAllTextAsync(markerPath, JsonSerializer.Serialize(marker, JsonOptions), cancellationToken);

            var reusedRace = false;
            try
            {
                Directory.Move(stagingDirectory, outputDirectory);
            }
            catch (IOException) when (Directory.Exists(outputDirectory))
            {
                if (!await ExistingBuildMatchesAsync(
                        outputDirectory, project.Name, fingerprint, spec, scripts, cancellationToken))
                    throw new CliInputException(
                        $"Another build created a conflicting output directory: {outputDirectory}");
                reusedRace = true;
            }

            return new ProjectBuildOutcome(validation, MakeBuildReport(
                outputDirectory, project.Name, fingerprint, spec, scripts, reusedRace));
        }
        finally
        {
            DeleteOwnedStagingDirectory(stagingDirectory, outputRoot);
        }
    }

    private static LoadedProject LoadProject(string projectPath)
    {
        var inputPath = Path.GetFullPath(projectPath);
        var projectFile = Directory.Exists(inputPath)
            ? Path.Combine(inputPath, ProjectFileName)
            : inputPath;

        if (!File.Exists(projectFile))
            throw new CliInputException($"Project file was not found: {projectFile}");

        var document = ReadProjectDocument(projectFile);
        if (document.Format is not (1 or 2 or 3))
            throw new CliInputException($"Unsupported project format {document.Format}; expected 1, 2, or 3.");
        ValidateProjectName(document.Name);

        var root = Path.GetDirectoryName(projectFile)
            ?? throw new CliInputException("Could not determine the project directory.");
        var packageManifest = ResolveContainedPath(root, document.PackageManifest, "package manifest");
        TuningSchemaPin? schemaPin = null;
        string? schemaPath = null;
        if (document.TuningSchema is { } tuningSchema)
        {
            if (document.Format < 2)
                throw new CliInputException("Project tuningSchema requires format 2 or later.");
            schemaPath = ResolveContainedPath(root, tuningSchema.Snapshot, "tuning schema snapshot");
            schemaPin = new TuningSchemaPin(schemaPath, tuningSchema.GameVersion, tuningSchema.Sha256);
        }
        if (document.ScriptArchives is null)
            throw new CliInputException("Project field 'scriptArchives' must be an array.");
        var scripts = new List<string>(document.ScriptArchives.Count);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var script in document.ScriptArchives)
        {
            var fullPath = ResolveContainedPath(root, script, "script archive");
            if (!string.Equals(Path.GetExtension(fullPath), ".ts4script", StringComparison.OrdinalIgnoreCase))
                throw new CliInputException($"Script archive must use the .ts4script extension: {script}");
            if (!seenPaths.Add(fullPath))
                throw new CliInputException($"Script archive is listed more than once: {script}");
            scripts.Add(fullPath);
        }

        var dependencies = new List<ProjectDependency>();
        if (document.Dependencies is not null)
        {
            if (document.Format != 3)
                throw new CliInputException("Project dependencies require format 3.");
            var seen = new HashSet<(ResourceId Source, ResourceId Target)>();
            foreach (var dependency in document.Dependencies)
            {
                if (dependency is null)
                    throw new CliInputException("Project dependencies cannot contain null entries.");
                var source = ResourceIdParser.ParseTgi(dependency.Source);
                var target = ResourceIdParser.ParseTgi(dependency.Target);
                if (dependency.Requirement is not ("observed" or "bundled"))
                    throw new CliInputException("Dependency requirement must be 'observed' or 'bundled'.");
                if (dependency.Requirement == "bundled" && string.IsNullOrWhiteSpace(dependency.Reason))
                    throw new CliInputException("Bundled dependencies require a non-empty reason.");
                if (!seen.Add((source, target)))
                    throw new CliInputException($"Duplicate dependency assertion: {source} -> {target}.");
                dependencies.Add(new ProjectDependency(source, target, dependency.Requirement, dependency.Reason));
            }
        }

        return new LoadedProject(projectFile, root, document.Name, packageManifest, scripts, schemaPin, schemaPath, dependencies);
    }

    private static ProjectDocument ReadProjectDocument(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ProjectDocument>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
                ?? throw new CliInputException("Project file must contain a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new CliInputException($"Invalid project file: {exception.Message}");
        }
    }

    private static void ValidateProjectName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 ||
            !IsAsciiLetterOrDigit(name[0]) ||
            name.Any(character =>
                !IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.'))
            throw new CliInputException(
                "Project name must start with an ASCII letter or digit and contain only letters, digits, '.', '_' or '-'.");
    }

    private static bool IsAsciiLetterOrDigit(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static string ResolveContainedPath(string root, string relativePath, string description)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathFullyQualified(relativePath))
            throw new CliInputException($"Project {description} must be a relative path.");

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var relativeToRoot = Path.GetRelativePath(fullRoot, fullPath);
        var escapesRoot = Path.IsPathRooted(relativeToRoot) ||
                          relativeToRoot == ".." ||
                          relativeToRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                          relativeToRoot.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        if (escapesRoot)
            throw new CliInputException($"Project {description} escapes the project directory: {relativePath}");

        PathGuard.EnsureWithinDirectory(fullPath, fullRoot, $"Project {description}");

        return fullPath;
    }

    private static void ValidateScriptArchive(byte[] content)
    {
        using var file = new MemoryStream(content, writable: false);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        var seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasPythonModule = false;

        foreach (var entry in archive.Entries)
        {
            var normalizedName = entry.FullName.Replace('\\', '/');
            if (normalizedName.StartsWith("/", StringComparison.Ordinal) ||
                normalizedName.Contains(':') ||
                normalizedName.Split('/').Any(segment => segment == ".."))
                throw new InvalidDataException($"Archive entry has an unsafe path: {entry.FullName}");
            if (!seenEntries.Add(normalizedName))
                throw new InvalidDataException($"Archive contains a duplicate entry: {entry.FullName}");

            var extension = Path.GetExtension(normalizedName);
            if (string.Equals(extension, ".py", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".pyc", StringComparison.OrdinalIgnoreCase))
                hasPythonModule = true;
        }

        if (!hasPythonModule)
            throw new InvalidDataException("Archive contains no .py or .pyc modules.");
    }

    private static string ComputeFingerprint(
        string projectName,
        BuildSpec spec,
        IReadOnlyList<ScriptArtifact> scripts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendText(hash, projectName);

        foreach (var resource in spec.Resources.OrderBy(resource => resource.Id.ToString(), StringComparer.Ordinal))
        {
            AppendText(hash, resource.Id.ToString());
            AppendBytes(hash, resource.Content.Span);
        }

        foreach (var script in scripts.OrderBy(script => script.RelativePath, StringComparer.Ordinal))
        {
            AppendText(hash, script.RelativePath);
            AppendBytes(hash, script.Content);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendText(IncrementalHash hash, string value) =>
        AppendBytes(hash, Encoding.UTF8.GetBytes(value));

    private static void AppendBytes(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> lengthPrefix = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(lengthPrefix, value.Length);
        hash.AppendData(lengthPrefix);
        hash.AppendData(value);
    }

    private async Task<bool> ExistingBuildMatchesAsync(
        string outputDirectory,
        string projectName,
        string fingerprint,
        BuildSpec spec,
        IReadOnlyList<ScriptArtifact> scripts,
        CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(outputDirectory, BuildMarkerName);
        if (!File.Exists(markerPath))
            return false;

        BuildMarker marker;
        try
        {
            marker = JsonSerializer.Deserialize<BuildMarker>(
                         await File.ReadAllTextAsync(markerPath, Encoding.UTF8, cancellationToken),
                         JsonOptions)
                     ?? throw new JsonException("Build marker was empty.");
        }
        catch (JsonException)
        {
            return false;
        }

        if (marker.Format != 1 ||
            !string.Equals(marker.ProjectName, projectName, StringComparison.Ordinal) ||
            !string.Equals(marker.Fingerprint, fingerprint, StringComparison.Ordinal))
            return false;

        if (spec.Resources.Count == 0)
        {
            if (marker.PackageFile is not null)
                return false;
        }
        else
        {
            if (marker.PackageFile is null ||
                !string.Equals(marker.PackageFile, $"{projectName}.package", StringComparison.Ordinal))
                return false;
            var packagePath = Path.Combine(outputDirectory, marker.PackageFile);
            if (!File.Exists(packagePath) ||
                !(await packageWork.VerifyAsync(packagePath, spec, cancellationToken)).Valid)
                return false;
        }

        var expectedScriptFiles = scripts
            .Select(script => script.RelativePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (marker.ScriptFiles is null)
            return false;
        var markerScriptFiles = marker.ScriptFiles
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (!expectedScriptFiles.SequenceEqual(markerScriptFiles, StringComparer.Ordinal))
            return false;

        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            BuildMarkerName
        };
        if (marker.PackageFile is not null)
            expectedFiles.Add(marker.PackageFile);
        foreach (var scriptFile in expectedScriptFiles)
            expectedFiles.Add(scriptFile);

        var actualFiles = Directory.GetFiles(outputDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(outputDirectory, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedFiles.SetEquals(actualFiles))
            return false;

        foreach (var script in scripts)
        {
            var destination = Path.Combine(
                outputDirectory,
                script.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(destination))
                return false;
            var actual = await File.ReadAllBytesAsync(destination, cancellationToken);
            if (!actual.AsSpan().SequenceEqual(script.Content))
                return false;
        }

        return true;
    }

    private static ProjectBuildReport MakeBuildReport(
        string outputDirectory,
        string projectName,
        string fingerprint,
        BuildSpec spec,
        IReadOnlyList<ScriptArtifact> scripts,
        bool reused)
    {
        var package = spec.Resources.Count == 0
            ? null
            : Path.Combine(outputDirectory, $"{projectName}.package");
        return new ProjectBuildReport(
            outputDirectory,
            package,
            spec.Resources.Count,
            scripts.Count,
            fingerprint,
            reused);
    }

    private static void WriteIfAbsent(string path, string content)
    {
        if (File.Exists(path))
            return;

        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path);
        }
        catch (IOException) when (File.Exists(path))
        {
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void DeleteOwnedStagingDirectory(string stagingDirectory, string outputRoot)
    {
        var fullStage = Path.GetFullPath(stagingDirectory);
        var fullRoot = Path.GetFullPath(outputRoot);
        var relative = Path.GetRelativePath(fullRoot, fullStage);
        if (relative.StartsWith(".s4mt-stage-", StringComparison.Ordinal) &&
            !relative.Contains(Path.DirectorySeparatorChar) &&
            Directory.Exists(fullStage))
            Directory.Delete(fullStage, recursive: true);
    }

    private static PreparedProject InvalidPreparedProject(
        string projectRoot,
        ProjectDiagnostic diagnostic)
    {
        var report = new ProjectValidationReport(false, projectRoot, 0, 0, [diagnostic]);
        return new PreparedProject(null, null, [], report);
    }

    private sealed class ProjectDocument
    {
        public required int Format { get; init; }
        public required string Name { get; init; }
        public required string PackageManifest { get; init; }
        public required List<string> ScriptArchives { get; init; }
        public TuningSchemaDocument? TuningSchema { get; init; }
        public List<ProjectDependencyDocument?>? Dependencies { get; init; }
    }

    private sealed class ProjectDependencyDocument
    {
        public required string Source { get; init; }
        public required string Target { get; init; }
        public required string Requirement { get; init; }
        public string? Reason { get; init; }
    }

    private sealed record ProjectDependency(ResourceId Source, ResourceId Target, string Requirement, string? Reason);

    private sealed class TuningSchemaDocument
    {
        public required string Snapshot { get; init; }
        public required string GameVersion { get; init; }
        public required string Sha256 { get; init; }
    }

    private sealed class BuildMarker
    {
        public required int Format { get; init; }
        public required string ProjectName { get; init; }
        public required string Fingerprint { get; init; }
        public string? PackageFile { get; init; }
        public required List<string> ScriptFiles { get; init; }
    }

    private sealed record LoadedProject(
        string ProjectFilePath,
        string Root,
        string Name,
        string PackageManifestPath,
        IReadOnlyList<string> ScriptArchivePaths,
        TuningSchemaPin? TuningSchema,
        string? TuningSchemaPath,
        IReadOnlyList<ProjectDependency> Dependencies);

    private sealed record ScriptArtifact(string RelativePath, string SourcePath, byte[] Content);

    private sealed record PreparedProject(
        LoadedProject? Project,
        BuildSpec? Spec,
        IReadOnlyList<ScriptArtifact> Scripts,
        ProjectValidationReport Validation);

    private enum ProjectResourceKind
    {
        Opaque,
        TuningXml,
        SimData,
        Image,
        StringTable
    }
}
