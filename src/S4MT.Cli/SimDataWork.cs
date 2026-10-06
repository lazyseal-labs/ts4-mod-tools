using System.Security.Cryptography;
using System.Globalization;
using System.Collections.Immutable;
using System.Text.Json;
using LlamaLogic.Packages;
using LlamaLogic.Packages.Models;
using LlamaLogic.Packages.Models.Data;

namespace S4MT.Cli;

internal sealed class SimDataWork
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal string? ValidatePayload(ResourceId resourceId, ReadOnlyMemory<byte> payload)
    {
        EnsureSimData(resourceId);
        try
        {
            var decoded = DataModel.Decode(payload);
            var encoded = decoded.Encode();
            var verified = DataModel.Decode(encoded);
            if (!encoded.Span.SequenceEqual(verified.Encode().Span))
                return $"SimData resource {resourceId} did not remain stable through LlamaLogic encode and decode.";

            return null;
        }
        catch (Exception exception) when (IsExpectedDecodeFailure(exception))
        {
            return $"LlamaLogic could not validate SimData resource {resourceId} ({exception.GetType().Name}: {exception.Message}); it remains available as opaque bytes.";
        }
    }

    public async Task<SimDataInspectReport> InspectAsync(
        string packagePath,
        ResourceId resourceId,
        CancellationToken cancellationToken)
    {
        EnsureSimData(resourceId);
        var fullPackagePath = Path.GetFullPath(packagePath);
        await using var package = await DataBasePackedFile.FromPathAsync(fullPackagePath);
        var key = ToPackageKey(resourceId);
        if (!await package.ContainsKeyAsync(key, cancellationToken))
            throw new CliInputException($"Package does not contain SimData resource {resourceId}.");
        var model = await package.GetDataAsync(key, force: false, cancellationToken);
        var description = Describe(model);
        return new SimDataInspectReport(
            fullPackagePath,
            resourceId.ToString(),
            model.ResourceName,
            model.Version.ToString(),
            description.Length,
            description.Sum(table => table.RowCount),
            description);
    }

    public async Task<SimDataRoundtripReport> RoundtripAsync(
        string packagePath,
        ResourceId resourceId,
        string outputPath,
        CancellationToken cancellationToken)
    {
        EnsureSimData(resourceId);
        var fullPackagePath = Path.GetFullPath(packagePath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        PathGuard.EnsureDoesNotOverwrite(fullOutputPath, (fullPackagePath, "source package"));

        var outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var sourceDescription = Array.Empty<SimDataTableDescription>();
            ReadOnlyMemory<byte> sourceEncoding;
            string? resourceName;
            string version;
            await using (var source = await DataBasePackedFile.FromPathAsync(fullPackagePath))
            {
                var key = ToPackageKey(resourceId);
                if (!await source.ContainsKeyAsync(key, cancellationToken))
                    throw new CliInputException($"Source package does not contain SimData resource {resourceId}.");

                var model = await source.GetDataAsync(key, force: false, cancellationToken);
                sourceDescription = Describe(model);
                sourceEncoding = model.Encode();
                resourceName = model.ResourceName;
                version = model.Version.ToString();

                await using (var output = new DataBasePackedFile())
                {
                    await output.SetAsync(key, model, CompressionMode.Auto, cancellationToken);
                    await output.SaveAsAsync(temporaryPath);
                }

                await using var candidate = await DataBasePackedFile.FromPathAsync(temporaryPath);
                var keys = await candidate.GetKeysAsync(cancellationToken: cancellationToken);
                if (keys.Count != 1 || keys[0] != key)
                    throw new InvalidDataException("SimData round-trip did not preserve its exact resource key.");

                var verifiedModel = await candidate.GetDataAsync(key, force: false, cancellationToken);
                var verifiedEncoding = verifiedModel.Encode();
                if (!sourceEncoding.Span.SequenceEqual(verifiedEncoding.Span))
                    throw new InvalidDataException($"SimData model encode/decode changed its table data for {resourceId}.");
                if (!string.Equals(
                        JsonSerializer.Serialize(sourceDescription, JsonOptions),
                        JsonSerializer.Serialize(Describe(verifiedModel), JsonOptions),
                        StringComparison.Ordinal))
                    throw new InvalidDataException($"SimData schema read-back did not match for {resourceId}.");
            }

            var digest = await ComputeSha256Async(temporaryPath, cancellationToken);
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            return new SimDataRoundtripReport(
                fullPackagePath,
                fullOutputPath,
                resourceId.ToString(),
                resourceName,
                version,
                sourceDescription.Length,
                sourceDescription.Sum(table => table.RowCount),
                digest,
                Verified: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<SimDataSetReport> SetScalarAsync(
        string packagePath,
        ResourceId resourceId,
        string tableName,
        int rowIndex,
        string columnName,
        string valueText,
        string outputPath,
        CancellationToken cancellationToken)
    {
        EnsureSimData(resourceId);
        if (string.IsNullOrWhiteSpace(tableName))
            throw new CliInputException("SimData table name must not be empty.");
        if (string.IsNullOrWhiteSpace(columnName))
            throw new CliInputException("SimData column name must not be empty.");
        if (rowIndex < 0)
            throw new CliInputException("SimData row index must be zero or greater.");

        var fullPackagePath = Path.GetFullPath(packagePath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        PathGuard.EnsureDoesNotOverwrite(fullOutputPath, (fullPackagePath, "source package"));

        var outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            int originalResourceCount;
            string? resourceName;
            string version;
            string dataType;
            string oldValue;
            string newValue;
            ReadOnlyMemory<byte> expectedEncoding;
            DataModel baselineModel;
            DataModel editedModel;
            byte[] originalPayload;
            int selectedTableIndex;
            int selectedColumnIndex;
            object parsedValue;

            await using (var source = await DataBasePackedFile.FromPathAsync(fullPackagePath))
            {
                var key = ToPackageKey(resourceId);
                if (!await source.ContainsKeyAsync(key, cancellationToken))
                    throw new CliInputException($"Source package does not contain SimData resource {resourceId}.");

                var originalKeys = (await source.GetKeysAsync(cancellationToken: cancellationToken)).ToHashSet();
                originalResourceCount = originalKeys.Count;
                originalPayload = (await source.GetAsync(key, force: false, cancellationToken)).ToArray();
                baselineModel = DataModel.Decode(originalPayload);
                editedModel = DataModel.Decode(originalPayload);
                resourceName = editedModel.ResourceName;
                version = editedModel.Version.ToString();
                (selectedTableIndex, var selectedTable) = FindUniqueTable(editedModel, tableName);
                if (rowIndex >= selectedTable.RowCount)
                    throw new CliInputException($"Row index {rowIndex} is outside table '{tableName}' (row count {selectedTable.RowCount}).");

                selectedColumnIndex = FindUniqueColumn(selectedTable, columnName);
                var type = selectedTable.GetColumnType(selectedColumnIndex);
                dataType = type.ToString();
                var oldCell = selectedTable.Get(rowIndex, selectedColumnIndex);
                oldValue = FormatValue(oldCell);
                parsedValue = ParseCellValue(type, valueText, oldCell);
                newValue = FormatValue(parsedValue);
                selectedTable.Set(rowIndex, selectedColumnIndex, parsedValue);

                if (!ValuesEqual(parsedValue, selectedTable.Get(rowIndex, selectedColumnIndex)))
                    throw new InvalidDataException("SimData scalar value did not match after in-memory assignment.");
                VerifySameSchema(baselineModel, editedModel, resourceId, allowStringTableLengthChange: type is DataModelType.STRING8);
                VerifyScalarCellsEqual(
                    baselineModel,
                    editedModel,
                    selectedTableIndex,
                    rowIndex,
                    selectedColumnIndex,
                    resourceId);
                VerifyReferencesRemainValid(baselineModel, editedModel, resourceId);
                expectedEncoding = editedModel.Encode();

                await source.SetAsync(key, editedModel, CompressionMode.Auto, cancellationToken);
                await source.SaveAsAsync(temporaryPath);

                await using var candidate = await DataBasePackedFile.FromPathAsync(temporaryPath);
                var candidateKeys = (await candidate.GetKeysAsync(cancellationToken: cancellationToken)).ToHashSet();
                if (!originalKeys.SetEquals(candidateKeys))
                    throw new InvalidDataException("SimData set changed the package resource-key set.");

                foreach (var unchangedKey in originalKeys.Where(existingKey => existingKey != key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var before = await source.GetAsync(unchangedKey, force: false, cancellationToken);
                    var after = await candidate.GetAsync(unchangedKey, force: false, cancellationToken);
                    if (!before.Span.SequenceEqual(after.Span))
                        throw new InvalidDataException($"SimData set changed unrelated resource {ToResourceId(unchangedKey)}.");
                }

                var verifiedModel = await candidate.GetDataAsync(key, force: false, cancellationToken);
                if (!expectedEncoding.Span.SequenceEqual(verifiedModel.Encode().Span))
                    throw new InvalidDataException($"SimData set model encoding did not match after package read-back for {resourceId}.");
                VerifySameSchema(baselineModel, verifiedModel, resourceId, allowStringTableLengthChange: type is DataModelType.STRING8);
                VerifyScalarCellsEqual(
                    baselineModel,
                    verifiedModel,
                    selectedTableIndex,
                    rowIndex,
                    selectedColumnIndex,
                    resourceId);
                VerifyReferencesRemainValid(baselineModel, verifiedModel, resourceId);
                var verifiedTable = verifiedModel.Tables[selectedTableIndex];
                if (!ValuesEqual(parsedValue, verifiedTable.Get(rowIndex, selectedColumnIndex)))
                    throw new InvalidDataException("SimData set value did not match after package read-back.");
            }

            var digest = await ComputeSha256Async(temporaryPath, cancellationToken);
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            return new SimDataSetReport(
                fullPackagePath,
                fullOutputPath,
                resourceId.ToString(),
                resourceName,
                version,
                tableName,
                rowIndex,
                columnName,
                dataType,
                oldValue,
                newValue,
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

    private static SimDataTableDescription[] Describe(DataModel model) => model.Tables
        .Select(table => new SimDataTableDescription(
            table.Name,
            table.SchemaName,
            table.SchemaHash,
            table.RowCount,
            table.ColumnCount,
            Enumerable.Range(0, table.ColumnCount)
                .Select(columnIndex => new SimDataColumnDescription(
                    table.GetColumnName(columnIndex),
                    table.GetColumnType(columnIndex).ToString(),
                    table.GetColumnFlags(columnIndex)))
                .ToArray()))
        .ToArray();

    private static (int Index, DataModelTable Table) FindUniqueTable(DataModel model, string tableName)
    {
        var matches = model.Tables
            .Select((table, index) => (table, index))
            .Where(candidate => string.Equals(candidate.table.Name, tableName, StringComparison.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            1 => (matches[0].index, matches[0].table),
            0 => throw new CliInputException($"SimData resource does not contain table '{tableName}'."),
            _ => throw new CliInputException($"SimData resource contains more than one table named '{tableName}'.")
        };
    }

    private static int FindUniqueColumn(DataModelTable table, string columnName)
    {
        var matches = Enumerable.Range(0, table.ColumnCount)
            .Where(index => string.Equals(table.GetColumnName(index), columnName, StringComparison.Ordinal))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new CliInputException($"SimData table '{table.Name}' does not contain column '{columnName}'."),
            _ => throw new CliInputException($"SimData table '{table.Name}' contains more than one column named '{columnName}'.")
        };
    }

    private static object ParseCellValue(DataModelType type, string value, object? oldCell) => type switch
    {
        DataModelType.BOOL => bool.TryParse(value, out var parsedBool)
            ? parsedBool
            : throw InvalidScalar(type, value),
        DataModelType.INT8 => sbyte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt8)
            ? parsedInt8
            : throw InvalidScalar(type, value),
        DataModelType.UINT8 => byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedUInt8)
            ? parsedUInt8
            : throw InvalidScalar(type, value),
        DataModelType.INT16 => short.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt16)
            ? parsedInt16
            : throw InvalidScalar(type, value),
        DataModelType.UINT16 => ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedUInt16)
            ? parsedUInt16
            : throw InvalidScalar(type, value),
        DataModelType.INT32 => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt32)
            ? parsedInt32
            : throw InvalidScalar(type, value),
        DataModelType.UINT32 => uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedUInt32)
            ? parsedUInt32
            : throw InvalidScalar(type, value),
        DataModelType.INT64 => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt64)
            ? parsedInt64
            : throw InvalidScalar(type, value),
        DataModelType.UINT64 => ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedUInt64)
            ? parsedUInt64
            : throw InvalidScalar(type, value),
        DataModelType.FLOAT => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFloat) && float.IsFinite(parsedFloat)
            ? parsedFloat
            : throw InvalidScalar(type, value),
        DataModelType.STRING8 or DataModelType.HASHEDSTRING8 => SetString8(oldCell, value),
        DataModelType.FLOAT3 => ParseFixedFloatVector(type, value, oldCell, 3),
        DataModelType.FLOAT4 => ParseFixedFloatVector(type, value, oldCell, 4),
        _ => throw new CliInputException($"SimData column type '{type}' is not a supported editable cell type.")
    };

    private static ImmutableArray<float> ParseFixedFloatVector(
        DataModelType type,
        string value,
        object? oldCell,
        int componentCount)
    {
        if (oldCell is not ImmutableArray<float> previous || previous.Length != componentCount)
            throw new CliInputException($"SimData {type} edits require an existing fixed vector with {componentCount} components.");

        var components = value.Split(',', StringSplitOptions.TrimEntries);
        if (components.Length != componentCount)
            throw new CliInputException($"SimData {type} values must provide exactly {componentCount} comma-separated numbers.");

        var parsed = ImmutableArray.CreateBuilder<float>(componentCount);
        foreach (var component in components)
        {
            if (!float.TryParse(component, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                !float.IsFinite(number))
                throw new CliInputException($"SimData {type} component '{component}' is invalid or non-finite.");
            parsed.Add(number);
        }

        return parsed.MoveToImmutable();
    }

    private static DataModelString SetString8(object? oldCell, string value)
    {
        if (value.Any(character => character == '\0' || character > 0x7F))
            throw new CliInputException("SimData STRING8 values must contain only ASCII characters and no null character.");

        if (oldCell is not DataModelString { IsValid: true } stringReference)
            throw new CliInputException("SimData STRING8 edits require an existing valid string reference.");

        stringReference.Value = value;
        return stringReference;
    }

    private static CliInputException InvalidScalar(DataModelType type, string value) =>
        new($"Value '{value}' is invalid or out of range for SimData type '{type}'.");

    private static void VerifySameSchema(
        DataModel expected,
        DataModel actual,
        ResourceId resourceId,
        bool allowStringTableLengthChange = false)
    {
        var expectedDescription = Describe(expected);
        var actualDescription = Describe(actual);
        if (allowStringTableLengthChange)
        {
            for (var tableIndex = 0; tableIndex < expected.Tables.Count; tableIndex++)
            {
                if (IsRawStringTable(expected.Tables[tableIndex]) && IsRawStringTable(actual.Tables[tableIndex]))
                {
                    expectedDescription[tableIndex] = expectedDescription[tableIndex] with { RowCount = -1 };
                    actualDescription[tableIndex] = actualDescription[tableIndex] with { RowCount = -1 };
                }
            }
        }

        var expectedJson = JsonSerializer.Serialize(expectedDescription, JsonOptions);
        var actualJson = JsonSerializer.Serialize(actualDescription, JsonOptions);
        if (!string.Equals(expectedJson, actualJson, StringComparison.Ordinal))
            throw new InvalidDataException($"SimData set changed the table schemas for {resourceId}.");
    }

    private static bool IsRawStringTable(DataModelTable table) =>
        table.Name is null &&
        table.SchemaName is null &&
        table.SchemaHash is null &&
        table.ColumnCount == 1 &&
        table.GetColumnType(0) is DataModelType.CHAR8;

    private static void VerifyScalarCellsEqual(
        DataModel expected,
        DataModel actual,
        int selectedTableIndex,
        int selectedRowIndex,
        int selectedColumnIndex,
        ResourceId resourceId)
    {
        if (expected.Tables.Count != actual.Tables.Count)
            throw new InvalidDataException($"SimData set changed table count for {resourceId}.");

        for (var tableIndex = 0; tableIndex < expected.Tables.Count; tableIndex++)
        {
            var expectedTable = expected.Tables[tableIndex];
            var actualTable = actual.Tables[tableIndex];
            for (var columnIndex = 0; columnIndex < expectedTable.ColumnCount; columnIndex++)
            {
                var type = expectedTable.GetColumnType(columnIndex);
                if (!IsSupportedScalar(type))
                    continue;
                for (var rowIndex = 0; rowIndex < expectedTable.RowCount; rowIndex++)
                {
                    if (tableIndex == selectedTableIndex && rowIndex == selectedRowIndex && columnIndex == selectedColumnIndex)
                        continue;
                    if (!ValuesEqual(
                            expectedTable.Get(rowIndex, columnIndex),
                            actualTable.Get(rowIndex, columnIndex)))
                        throw new InvalidDataException(
                            $"SimData set changed scalar cell in table '{expectedTable.Name}', row {rowIndex}, column '{expectedTable.GetColumnName(columnIndex)}'.");
                }
            }
        }
    }

    private static bool IsSupportedScalar(DataModelType type) => type is
        DataModelType.BOOL or
        DataModelType.INT8 or DataModelType.UINT8 or
        DataModelType.INT16 or DataModelType.UINT16 or
        DataModelType.INT32 or DataModelType.UINT32 or
        DataModelType.INT64 or DataModelType.UINT64 or
        DataModelType.FLOAT or
        DataModelType.FLOAT3 or DataModelType.FLOAT4;

    private static void VerifyReferencesRemainValid(DataModel expected, DataModel actual, ResourceId resourceId)
    {
        for (var tableIndex = 0; tableIndex < expected.Tables.Count; tableIndex++)
        {
            var expectedTable = expected.Tables[tableIndex];
            var actualTable = actual.Tables[tableIndex];
            for (var columnIndex = 0; columnIndex < expectedTable.ColumnCount; columnIndex++)
            {
                for (var rowIndex = 0; rowIndex < expectedTable.RowCount; rowIndex++)
                {
                    if (expectedTable.Get(rowIndex, columnIndex) is not DataModelReference { IsValid: true })
                        continue;
                    if (actualTable.Get(rowIndex, columnIndex) is not DataModelReference { IsValid: true })
                        throw new InvalidDataException(
                            $"SimData edit invalidated a previously valid reference in table '{expectedTable.Name}', row {rowIndex}, column '{expectedTable.GetColumnName(columnIndex)}' for {resourceId}.");
                }
            }
        }
    }

    private static bool ValuesEqual(object? expected, object? actual)
    {
        if (expected is DataModelString expectedString && actual is DataModelString actualString)
            return expectedString.IsValid == actualString.IsValid &&
                (!expectedString.IsValid || string.Equals(ReadString8(expectedString), ReadString8(actualString), StringComparison.Ordinal));

        if (expected is ImmutableArray<float> expectedVector && actual is ImmutableArray<float> actualVector)
            return expectedVector.AsSpan().SequenceEqual(actualVector.AsSpan());

        return expected is null ? actual is null : expected.Equals(actual);
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        DataModelString { IsValid: true } stringReference => ReadString8(stringReference),
        DataModelString => "null",
        ImmutableArray<float> vector => string.Join(',', vector.Select(component => component.ToString("R", CultureInfo.InvariantCulture))),
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static string ReadString8(DataModelString stringReference)
    {
        try
        {
            return stringReference.Value;
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new CliInputException("SimData STRING8 reference cannot be decoded by the current model codec.");
        }
    }

    private static void EnsureSimData(ResourceId id)
    {
        if (id.Type != unchecked((uint)ResourceType.SimData))
            throw new CliInputException($"Resource {id} is not a SimData resource.");
    }

    private static bool IsExpectedDecodeFailure(Exception exception) => exception is
        ArgumentException or
        EndOfStreamException or
        FormatException or
        IndexOutOfRangeException or
        InvalidDataException or
        InvalidOperationException or
        NotSupportedException or
        OverflowException or
        UnexpectedFormatDecodingException;

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
