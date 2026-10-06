using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

namespace S4MT.Cli;

internal sealed record TuningSchemaPin(string Snapshot, string GameVersion, string Sha256);
internal sealed record TuningReferenceObservation(string Field, string RawValue, ResourceId? Target, string Domain);

internal sealed class TuningSchemaValidator
{
    private const int RuleVersion = 1;
    private const int MaximumDiagnostics = 64;
    private readonly TDescSnapshotData snapshot;

    private TuningSchemaValidator(TDescSnapshotData snapshot) => this.snapshot = snapshot;

    internal static async Task<TuningSchemaValidator> OpenAsync(
        TuningSchemaPin pin,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pin.Snapshot))
            throw new CliInputException("Tuning schema snapshot path is required.");
        if (string.IsNullOrWhiteSpace(pin.GameVersion) ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                pin.GameVersion,
                "^[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new CliInputException("Tuning schema game version must have four numeric components.");
        if (!IsSha256(pin.Sha256))
            throw new CliInputException("Tuning schema snapshot SHA-256 must contain 64 hexadecimal digits.");

        var snapshot = await TDescSnapshotWork.LoadAsync(pin.Snapshot, cancellationToken);
        if (!string.Equals(snapshot.Manifest.GameVersion, pin.GameVersion, StringComparison.Ordinal))
            throw new CliInputException(
                $"Tuning schema game version mismatch. Requested {pin.GameVersion}, snapshot declares {snapshot.Manifest.GameVersion}.");
        if (!string.Equals(snapshot.Manifest.SnapshotSha256, pin.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new CliInputException("Tuning schema snapshot SHA-256 does not match the requested pin.");

        return new TuningSchemaValidator(snapshot);
    }

    internal async Task<TuningSchemaReport> ValidateAsync(
        XDocument document,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<TuningSchemaDiagnostic>();
        var root = document.Root;
        if (root is null || root.Name.LocalName != "I")
        {
            Add(diagnostics, Diagnostic("schema_tuning_root_unverified", "warning",
                "Schema checks currently cover Instance tunings only; this root remains unverified.",
                root, null, null));
            return Report("unverified", 0, 1, diagnostics);
        }

        var tuningClass = root.Attribute("c")?.Value;
        var module = root.Attribute("m")?.Value;
        var instanceType = root.Attribute("i")?.Value;
        if (string.IsNullOrWhiteSpace(tuningClass) || string.IsNullOrWhiteSpace(module) ||
            string.IsNullOrWhiteSpace(instanceType))
        {
            Add(diagnostics, Diagnostic("schema_identity_unverified", "warning",
                "Tuning needs class, module, and instance type metadata for schema lookup.",
                root, null, null));
            return Report("unverified", 0, 1, diagnostics);
        }

        var matches = snapshot.Index.Classes.Where(entry =>
                string.Equals(entry.DefinitionKind, "Instance", StringComparison.Ordinal) &&
                string.Equals(entry.InstanceType, instanceType, StringComparison.Ordinal) &&
                string.Equals(entry.Module, module, StringComparison.Ordinal) &&
                string.Equals(entry.Class, tuningClass, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            Add(diagnostics, Diagnostic("schema_definition_unavailable", "warning",
                $"No Instance schema matches {instanceType}:{module}:{tuningClass}; tuning values remain unverified.",
                root, null, null));
            return Report("unverified", 0, 1, diagnostics);
        }
        if (matches.Length != 1)
        {
            Add(diagnostics, Diagnostic("schema_definition_ambiguous", "warning",
                $"More than one Instance schema matches {instanceType}:{module}:{tuningClass}; tuning values remain unverified.",
                root, null, null));
            return Report("unverified", 0, 1, diagnostics);
        }

        var entry = matches[0];
        var definition = await TDescSnapshotWork.LoadDefinitionAsync(snapshot, entry, cancellationToken);
        var checkedValueCount = 0;
        var unverifiedCount = 0;
        var fieldsByName = new Dictionary<string, List<TDescNode>>(StringComparer.Ordinal);
        foreach (var field in definition.Fields)
        {
            if (field.Name is not { Length: > 0 } name)
                continue;
            if (!fieldsByName.TryGetValue(name, out var schemaFields))
                fieldsByName.Add(name, schemaFields = []);
            schemaFields.Add(field);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in root.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = (string?)value.Attribute("n");
            if (string.IsNullOrWhiteSpace(name))
            {
                Add(diagnostics, Diagnostic("schema_named_field_required", "warning",
                    "This field has no name, so it cannot be matched to the schema.",
                    value, null, definition.SourceFile, definition.Line));
                unverifiedCount++;
                continue;
            }
            if (!seen.Add(name))
            {
                Add(diagnostics, Diagnostic("schema_duplicate_field", "error",
                    $"Tuning field '{name}' occurs more than once.", value, name, definition.SourceFile, definition.Line));
                continue;
            }
            if (!fieldsByName.TryGetValue(name, out var candidates))
            {
                Add(diagnostics, Diagnostic("schema_field_unknown", "warning",
                    $"Field '{name}' is not defined by the pinned schema; it remains unverified.",
                    value, name, definition.SourceFile, definition.Line));
                unverifiedCount++;
                continue;
            }
            if (candidates.Count != 1)
            {
                Add(diagnostics, Diagnostic("schema_field_ambiguous", "warning",
                    $"Field '{name}' has ambiguous schema declarations and remains unverified.",
                    value, name, definition.SourceFile, candidates[0].Line));
                unverifiedCount++;
                continue;
            }

            ValidateValue(candidates[0], value, name, definition.SourceFile,
                diagnostics, ref checkedValueCount, ref unverifiedCount);
        }

        var coverage = unverifiedCount == 0 && diagnostics.All(item => item.Severity != "warning")
            ? "checked"
            : "partial";
        return Report(coverage, checkedValueCount, unverifiedCount, diagnostics);
    }

    internal async Task<IReadOnlyList<TuningReferenceObservation>> ExtractTopLevelReferencesAsync(
        XDocument document,
        CancellationToken cancellationToken)
    {
        var root = document.Root;
        if (root?.Name.LocalName != "I")
            return [];
        var tuningClass = root.Attribute("c")?.Value;
        var module = root.Attribute("m")?.Value;
        var instanceType = root.Attribute("i")?.Value;
        if (string.IsNullOrWhiteSpace(tuningClass) || string.IsNullOrWhiteSpace(module) ||
            string.IsNullOrWhiteSpace(instanceType))
            return [];

        var matches = snapshot.Index.Classes.Where(entry =>
                string.Equals(entry.DefinitionKind, "Instance", StringComparison.Ordinal) &&
                string.Equals(entry.InstanceType, instanceType, StringComparison.Ordinal) &&
                string.Equals(entry.Module, module, StringComparison.Ordinal) &&
                string.Equals(entry.Class, tuningClass, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            return [];

        var definition = await TDescSnapshotWork.LoadDefinitionAsync(snapshot, matches[0], cancellationToken);
        var fieldsByName = definition.Fields
            .Where(field => !string.IsNullOrWhiteSpace(field.Name))
            .GroupBy(field => field.Name!, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var observations = new List<TuningReferenceObservation>();
        foreach (var value in root.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = (string?)value.Attribute("n");
            if (name is null || !fieldsByName.TryGetValue(name, out var declaration))
                continue;
            ExtractReferences(declaration, value, name, observations);
        }
        return observations;
    }

    private static void ExtractReferences(
        TDescNode declaration,
        XElement value,
        string fieldPath,
        List<TuningReferenceObservation> observations)
    {
        if (declaration.Element == "Tunable" && value.Name.LocalName == "T")
        {
            var raw = value.Value.Trim();
            if (declaration.Class == "TunableResourceKey" && declaration.Type == "ResourceKey" &&
                TryParseResourceKey(raw, out var resourceType, out _))
            {
                if (declaration.Attributes.TryGetValue("resource_types", out var types) &&
                    !types.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries)
                        .Select(ResourceIdParser.ParseResourceType).Contains(resourceType))
                    return;
                observations.Add(new TuningReferenceObservation(fieldPath, raw,
                    ResourceIdParser.ParseTgi(raw), "resource_key"));
            }
            else if (declaration.Class == "TunableReference")
                observations.Add(new TuningReferenceObservation(fieldPath, raw, null,
                    declaration.Attributes.GetValueOrDefault("manager", "tuning_reference")));
            else if (declaration.Class == "TunableLocalizedString")
                observations.Add(new TuningReferenceObservation(fieldPath, raw, null, "string_table_key"));
            return;
        }

        if (declaration.Element == "TunableList" && value.Name.LocalName == "L" && declaration.Children.Count == 1)
        {
            var itemDeclaration = declaration.Children[0];
            for (var index = 0; index < value.Elements().Count(); index++)
                ExtractReferences(itemDeclaration, value.Elements().ElementAt(index), $"{fieldPath}[{index}]", observations);
            return;
        }

        if (declaration.Element is "TunableTuple" or "TunableMapping" && value.Name.LocalName == "U")
        {
            var declarations = declaration.Children.Where(child => child.Name is { Length: > 0 })
                .GroupBy(child => child.Name!, StringComparer.Ordinal)
                .Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            foreach (var child in value.Elements())
                if ((string?)child.Attribute("n") is { } name && declarations.TryGetValue(name, out var childDeclaration))
                    ExtractReferences(childDeclaration, child, $"{fieldPath}.{name}", observations);
        }
    }

    private TuningSchemaReport Report(
        string coverage,
        int checkedValues,
        int unverifiedValues,
        IReadOnlyList<TuningSchemaDiagnostic> diagnostics) => new(
            Valid: diagnostics.All(item => item.Severity != "error"),
            Coverage: coverage,
            GameVersion: snapshot.Manifest.GameVersion,
            SnapshotSha256: snapshot.Manifest.SnapshotSha256,
            RuleVersion: RuleVersion,
            CheckedValueCount: checkedValues,
            UnverifiedCount: unverifiedValues,
            Diagnostics: diagnostics,
            Limitations:
            [
                "Absent fields are not checked for requiredness.",
                "Resource, tuning, and string-table target existence is not checked.",
                "SimData companion-resource requirements are not checked.",
                "Coverage applies to values checked against supported declarations, not the full EA schema language."
            ]);

    private static void ValidateValue(
        TDescNode declaration,
        XElement value,
        string fieldPath,
        string sourceFile,
        List<TuningSchemaDiagnostic> diagnostics,
        ref int checkedValueCount,
        ref int unverifiedCount)
    {
        var expectedElement = declaration.Element switch
        {
            "Tunable" => "T",
            "TunableEnum" => "E",
            "TunableList" => "L",
            "TunableMapping" => "L",
            "TunableTuple" => "U",
            "TunableVariant" => "V",
            _ => null
        };

        if (expectedElement is null)
        {
            WarnUnsupported(diagnostics, value, fieldPath, sourceFile, declaration.Line,
                $"Schema node '{declaration.Element}' has no verified XML mapping.");
            unverifiedCount++;
            return;
        }
        if (value.Name.LocalName != expectedElement)
        {
            Add(diagnostics, Diagnostic("schema_shape_invalid", "error",
                $"Field '{fieldPath}' uses XML node '{value.Name.LocalName}'; the schema declares '{declaration.Element}' ({expectedElement}).",
                value, fieldPath, sourceFile, declaration.Line));
            return;
        }

        switch (declaration.Element)
        {
            case "Tunable":
                ValidateScalar(declaration, value, fieldPath, sourceFile,
                    diagnostics, ref checkedValueCount, ref unverifiedCount);
                return;
            case "TunableEnum":
                WarnUnsupported(diagnostics, value, fieldPath, sourceFile, declaration.Line,
                    "Enum names are not verified against enum module definitions yet.");
                unverifiedCount++;
                return;
            case "TunableList":
                ValidateList(declaration, value, fieldPath, sourceFile,
                    diagnostics, ref checkedValueCount, ref unverifiedCount);
                return;
            case "TunableMapping":
            case "TunableTuple":
                ValidateTuple(declaration, value, fieldPath, sourceFile,
                    diagnostics, ref checkedValueCount, ref unverifiedCount);
                return;
            case "TunableVariant":
                ValidateVariant(declaration, value, fieldPath, sourceFile,
                    diagnostics, ref checkedValueCount, ref unverifiedCount);
                return;
        }
    }

    private static void ValidateScalar(
        TDescNode declaration,
        XElement value,
        string fieldPath,
        string sourceFile,
        List<TuningSchemaDiagnostic> diagnostics,
        ref int checkedValueCount,
        ref int unverifiedCount)
    {
        var className = declaration.Class;
        var type = declaration.Type;
        if (className == "TunableLocalizedString" && type == "int")
        {
            if (TryReadUInt32(value.Value.Trim(), out _))
                checkedValueCount++;
            else
                Add(diagnostics, Diagnostic("schema_localized_string_key_invalid", "error",
                    $"Field '{fieldPath}' must be a 32-bit string-table key.", value, fieldPath, sourceFile, declaration.Line));
            return;
        }
        if (className == "TunableResourceKey" && type == "ResourceKey")
        {
            var raw = value.Value.Trim();
            if (IsNullAllowed(declaration) &&
                (raw.Length == 0 || string.Equals(raw, "None", StringComparison.OrdinalIgnoreCase)))
            {
                checkedValueCount++;
                return;
            }
            if (!TryParseResourceKey(raw, out var resourceType, out var tgiError))
            {
                Add(diagnostics, Diagnostic("schema_resource_key_invalid", "error",
                    $"Field '{fieldPath}' must contain a full hexadecimal type:group:instance resource key. {tgiError}",
                    value, fieldPath, sourceFile, declaration.Line));
                return;
            }
            if (declaration.Attributes.TryGetValue("resource_types", out var allowedTypes))
            {
                var parsedTypes = allowedTypes.Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Select(item => ResourceIdParser.ParseResourceType(item))
                    .ToArray();
                if (parsedTypes.Length == 0)
                {
                    WarnUnsupported(diagnostics, value, fieldPath, sourceFile, declaration.Line,
                        "The resource_types declaration is empty or unsupported; allowed types remain unverified.");
                    unverifiedCount++;
                    return;
                }
                if (!parsedTypes.Contains(resourceType))
                {
                    Add(diagnostics, Diagnostic("schema_resource_type_invalid", "error",
                        $"Resource key type 0x{resourceType:X8} is not allowed for field '{fieldPath}'.",
                        value, fieldPath, sourceFile, declaration.Line));
                    return;
                }
            }
            checkedValueCount++;
            return;
        }

        if (className == "TunableReference")
        {
            WarnUnsupported(diagnostics, value, fieldPath, sourceFile, declaration.Line,
                "Tuning references require type-specific identity and target checks, which are not enabled.");
            unverifiedCount++;
            return;
        }

        if (className is "TunableResourceKey" or "TunableLocalizedString")
        {
            WarnUnsupported(diagnostics, value, fieldPath, sourceFile, declaration.Line,
                $"The schema combines custom class '{className}' with primitive type '{type ?? "<missing>"}'.");
            unverifiedCount++;
            return;
        }

        if (value.HasElements)
        {
            WarnUnsupported(diagnostics, value, fieldPath, sourceFile, declaration.Line,
                "A scalar declaration contains nested XML and remains unverified.");
            unverifiedCount++;
            return;
        }

        var text = value.Value.Trim();
        var supported = type switch
        {
            "bool" => IsBoolean(text),
            "int" => BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _),
            "float" => TryParseFinite(text, out _),
            "str" => true,
            _ => false
        };

        if (!supported)
        {
            if (type is not ("bool" or "int" or "float" or "str"))
            {
                WarnUnsupported(diagnostics, value, fieldPath, sourceFile, declaration.Line,
                    $"Schema value type '{type ?? "<missing>"}' is not supported for field '{fieldPath}'.");
                unverifiedCount++;
                return;
            }
            Add(diagnostics, Diagnostic("schema_value_invalid", "error",
                $"Value of field '{fieldPath}' is not valid for schema type '{type}'.",
                value, fieldPath, sourceFile, declaration.Line));
            return;
        }

        if (type is "int" or "float")
        {
            if (declaration.Attributes.TryGetValue("min", out var minimum) && minimum != "None" &&
                !ValidateBound(minimum, type, value, fieldPath, sourceFile, declaration, diagnostics, true, out var minimumSupported))
            {
                if (!minimumSupported)
                    unverifiedCount++;
                return;
            }
            if (declaration.Attributes.TryGetValue("max", out var maximum) && maximum != "None" &&
                !ValidateBound(maximum, type, value, fieldPath, sourceFile, declaration, diagnostics, false, out var maximumSupported))
            {
                if (!maximumSupported)
                    unverifiedCount++;
                return;
            }
        }
        checkedValueCount++;
    }

    private static void ValidateList(
        TDescNode declaration,
        XElement value,
        string fieldPath,
        string sourceFile,
        List<TuningSchemaDiagnostic> diagnostics,
        ref int checkedValueCount,
        ref int unverifiedCount)
    {
        if (declaration.Children.Count != 1)
        {
            WarnUnsupported(diagnostics, value, fieldPath, sourceFile, declaration.Line,
                "List schemas without exactly one declared item shape are unsupported.");
            unverifiedCount++;
            return;
        }

        var itemRule = declaration.Children[0];
        var itemIndex = 0;
        foreach (var item in value.Elements())
        {
            if (item.Attribute("n") is not null)
            {
                Add(diagnostics, Diagnostic("schema_list_item_named", "warning",
                    "Named list items are outside the validated list form and remain unverified.",
                    item, $"{fieldPath}[{itemIndex}]", sourceFile, itemRule.Line));
                unverifiedCount++;
            }
            else
            {
                ValidateValue(itemRule, item, $"{fieldPath}[{itemIndex}]", sourceFile,
                    diagnostics, ref checkedValueCount, ref unverifiedCount);
            }
            itemIndex++;
        }
    }

    private static void ValidateTuple(
        TDescNode declaration,
        XElement value,
        string fieldPath,
        string sourceFile,
        List<TuningSchemaDiagnostic> diagnostics,
        ref int checkedValueCount,
        ref int unverifiedCount)
    {
        var childRules = declaration.Children
            .Where(child => !string.IsNullOrWhiteSpace(child.Name))
            .GroupBy(child => child.Name!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var childValue in value.Elements())
        {
            var childName = (string?)childValue.Attribute("n");
            if (string.IsNullOrWhiteSpace(childName))
            {
                Add(diagnostics, Diagnostic("schema_tuple_name_missing", "warning",
                    "Tuple child has no name and remains unverified.", childValue, fieldPath, sourceFile, declaration.Line));
                unverifiedCount++;
                continue;
            }
            if (!seen.Add(childName))
            {
                Add(diagnostics, Diagnostic("schema_duplicate_field", "error",
                    $"Tuple field '{fieldPath}.{childName}' occurs more than once.",
                    childValue, $"{fieldPath}.{childName}", sourceFile, declaration.Line));
                continue;
            }
            if (!childRules.TryGetValue(childName, out var matches))
            {
                Add(diagnostics, Diagnostic("schema_field_unknown", "warning",
                    $"Tuple field '{fieldPath}.{childName}' is not declared; it remains unverified.",
                    childValue, $"{fieldPath}.{childName}", sourceFile, declaration.Line));
                unverifiedCount++;
                continue;
            }
            if (matches.Length != 1)
            {
                Add(diagnostics, Diagnostic("schema_field_ambiguous", "warning",
                    $"Tuple field '{fieldPath}.{childName}' has ambiguous declarations.",
                    childValue, $"{fieldPath}.{childName}", sourceFile, matches[0].Line));
                unverifiedCount++;
                continue;
            }
            ValidateValue(matches[0], childValue, $"{fieldPath}.{childName}", sourceFile,
                diagnostics, ref checkedValueCount, ref unverifiedCount);
        }
    }

    private static void ValidateVariant(
        TDescNode declaration,
        XElement value,
        string fieldPath,
        string sourceFile,
        List<TuningSchemaDiagnostic> diagnostics,
        ref int checkedValueCount,
        ref int unverifiedCount)
    {
        var branchName = (string?)value.Attribute("t");
        if (branchName is null && declaration.Attributes.TryGetValue("default", out var defaultBranch))
            branchName = defaultBranch;
        if (string.IsNullOrWhiteSpace(branchName))
        {
            Add(diagnostics, Diagnostic("schema_variant_branch_missing", "warning",
                "Variant has no explicit or declared default branch; it remains unverified.",
                value, fieldPath, sourceFile, declaration.Line));
            unverifiedCount++;
            return;
        }

        var matches = declaration.Children.Where(child =>
            string.Equals(child.Name, branchName, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0)
        {
            Add(diagnostics, Diagnostic("schema_variant_branch_unknown", "error",
                $"Variant branch '{branchName}' is not declared for '{fieldPath}'.",
                value, fieldPath, sourceFile, declaration.Line));
            return;
        }
        if (matches.Length != 1)
        {
            Add(diagnostics, Diagnostic("schema_variant_branch_ambiguous", "warning",
                $"Variant branch '{branchName}' has ambiguous declarations.",
                value, fieldPath, sourceFile, matches[0].Line));
            unverifiedCount++;
            return;
        }

        var branch = matches[0];
        if (branch.Class == "TunableExistance" && !value.Elements().Any())
        {
            checkedValueCount++;
            return;
        }
        if (value.Elements().Count() != 1)
        {
            Add(diagnostics, Diagnostic("schema_variant_payload_invalid", "error",
                $"Variant branch '{fieldPath}.{branchName}' must have exactly one value node.",
                value, fieldPath, sourceFile, branch.Line));
            return;
        }

        var payload = value.Elements().Single();
        if (payload.Attribute("n") is { } payloadName && payloadName.Value != branchName)
        {
            Add(diagnostics, Diagnostic("schema_variant_payload_name_invalid", "error",
                $"Variant payload must be named '{branchName}'.", payload,
                $"{fieldPath}.{branchName}", sourceFile, branch.Line));
            return;
        }
        ValidateValue(branch, payload, $"{fieldPath}.{branchName}", sourceFile,
            diagnostics, ref checkedValueCount, ref unverifiedCount);
    }

    private static bool ValidateBound(
        string bound,
        string type,
        XElement value,
        string fieldPath,
        string sourceFile,
        TDescNode declaration,
        List<TuningSchemaDiagnostic> diagnostics,
        bool isMinimum,
        out bool comparisonCompleted)
    {
        comparisonCompleted = true;
        var raw = value.Value.Trim();
        var within = type switch
        {
            "int" when BigInteger.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) &&
                       BigInteger.TryParse(bound, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integerBound)
                => isMinimum ? integer >= integerBound : integer <= integerBound,
            "float" when TryParseFinite(raw, out var number) && TryParseFinite(bound, out var numberBound)
                => isMinimum ? number >= numberBound : number <= numberBound,
            _ => false
        };
        if (type == "int" && (!BigInteger.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _) ||
                              !BigInteger.TryParse(bound, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) ||
            type == "float" && (!TryParseFinite(raw, out _) || !TryParseFinite(bound, out _)))
        {
            comparisonCompleted = false;
            Add(diagnostics, Diagnostic("schema_range_unsupported", "warning",
                $"The numeric range for '{fieldPath}' cannot be interpreted safely; range validation is unverified.",
                value, fieldPath, sourceFile, declaration.Line));
            return false;
        }
        if (!within)
        {
            Add(diagnostics, Diagnostic("schema_range_invalid", "error",
                $"Value of field '{fieldPath}' is outside its declared range.",
                value, fieldPath, sourceFile, declaration.Line));
            return false;
        }
        return true;
    }

    private static bool IsNullAllowed(TDescNode declaration) =>
        declaration.Attributes.TryGetValue("allow_none", out var allowNone) &&
        string.Equals(allowNone, "True", StringComparison.OrdinalIgnoreCase);

    private static bool IsBoolean(string value) =>
        string.Equals(value, "True", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "False", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadUInt32(string value, out uint result)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result);
        return uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseFinite(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) && double.IsFinite(result);

    private static bool TryParseResourceKey(string value, out uint resourceType, out string error)
    {
        var parts = value.Split(':');
        if (parts.Length == 3 &&
            TryParseHex32(parts[0], out resourceType) &&
            TryParseHex32(parts[1], out _) &&
            TryParseHex64(parts[2], out _))
        {
            error = string.Empty;
            return true;
        }
        resourceType = 0;
        error = "";
        return false;
    }

    private static bool TryParseHex32(string value, out uint result)
    {
        var digits = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value.AsSpan(2) : value.AsSpan();
        if (digits.Length is >= 1 and <= 8 &&
            uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result))
            return true;
        result = 0;
        return false;
    }

    private static bool TryParseHex64(string value, out ulong result)
    {
        var digits = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value.AsSpan(2) : value.AsSpan();
        if (digits.Length is >= 1 and <= 16 &&
            ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result))
            return true;
        result = 0;
        return false;
    }

    private static TuningSchemaDiagnostic Diagnostic(
        string code,
        string severity,
        string message,
        XElement? value,
        string? xmlPath,
        string? schemaFile,
        int schemaLine = 0) => new(
            severity,
            code,
            message,
            xmlPath,
            value is System.Xml.IXmlLineInfo lineInfo && lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0,
            schemaFile,
            schemaLine);

    private static void WarnUnsupported(
        List<TuningSchemaDiagnostic> diagnostics,
        XElement value,
        string fieldPath,
        string sourceFile,
        int schemaLine,
        string reason) => Add(diagnostics, Diagnostic(
        "schema_field_unverified", "warning", $"Field '{fieldPath}' is not fully validated. {reason}",
        value, fieldPath, sourceFile, schemaLine));

    private static void Add(List<TuningSchemaDiagnostic> diagnostics, TuningSchemaDiagnostic diagnostic)
    {
        if (diagnostics.Count < MaximumDiagnostics)
        {
            diagnostics.Add(diagnostic);
            return;
        }
        if (diagnostics[^1].Code != "schema_diagnostics_truncated")
        {
            diagnostics[^1] = diagnostic with
            {
                Severity = "warning",
                Code = "schema_diagnostics_truncated",
                Message = "Additional schema diagnostics were omitted after the 64-item report limit."
            };
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

public sealed record TuningSchemaReport(
    bool Valid,
    string Coverage,
    string GameVersion,
    string SnapshotSha256,
    int RuleVersion,
    int CheckedValueCount,
    int UnverifiedCount,
    IReadOnlyList<TuningSchemaDiagnostic> Diagnostics,
    IReadOnlyList<string> Limitations);

public sealed record TuningSchemaDiagnostic(
    string Severity,
    string Code,
    string Message,
    string? XmlPath,
    int XmlLine,
    string? SchemaFile,
    int SchemaLine);
