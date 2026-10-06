using System.Xml;
using System.Xml.Linq;
using LlamaLogic.Packages;
using LlamaLogic.Packages.Cryptography;
using LlamaLogic.Packages.Models.Data;

namespace S4MT.Cli;

internal sealed record CombinedTuningExtractReport(
    string Package,
    string Xml,
    string Tgi,
    string Name,
    string? TuningClass,
    string? Module,
    int ExtractedElementCount);

internal sealed class CombinedTuningWork
{
    private const int MaximumExtractedElements = 250_000;
    private const int MaximumXmlDepth = 512;

    public async Task<CombinedTuningExtractReport> ExtractAsync(
        string packagePath,
        ResourceId combinedTuningId,
        string tuningName,
        string xmlPath,
        CancellationToken cancellationToken)
    {
        if (combinedTuningId.Type != 0x62E94D38)
            throw new CliInputException("Combined tuning extraction requires resource type 0x62E94D38.");
        if (string.IsNullOrWhiteSpace(tuningName))
            throw new CliInputException("A tuning name is required.");

        var fullPackagePath = Path.GetFullPath(packagePath);
        var fullXmlPath = Path.GetFullPath(xmlPath);
        PathGuard.EnsureDoesNotOverwrite(fullXmlPath, (fullPackagePath, "source package"));

        DataModel dataModel;
        await using (var package = await DataBasePackedFile.FromPathAsync(fullPackagePath))
        {
            var key = new ResourceKey(
                unchecked((ResourceType)combinedTuningId.Type),
                combinedTuningId.Group,
                combinedTuningId.Instance);
            if (!await package.ContainsKeyAsync(key, cancellationToken))
                throw new CliInputException($"Package does not contain CombinedTuning resource {combinedTuningId}.");
            dataModel = await package.GetDataAsync(key, force: false, cancellationToken);
        }

        var documentTable = dataModel.Tables.SingleOrDefault(table => table.SchemaName == "PackedXmlDocument")
            ?? throw new CliInputException("CombinedTuning payload is not a supported PackedXmlDocument.");
        var nodeTable = dataModel.Tables.SingleOrDefault(table => table.SchemaName == "PackedXmlNode")
            ?? throw new CliInputException("CombinedTuning payload does not contain its PackedXmlNode table.");
        var attributeTable = dataModel.Tables.SingleOrDefault(table => table.SchemaName == "PackedXmlAttribute")
            ?? throw new CliInputException("CombinedTuning payload does not contain its PackedXmlAttribute table.");

        var firstElement = documentTable.Get(0, "first_element") as DataModelObject;
        var topElement = documentTable.Get(0, "top_element") as DataModelObject;
        var stringVector = documentTable.Get(0, "string_table") as DataModelVector;
        if (firstElement is not { IsValid: true } || topElement is not { IsValid: true } || stringVector is null)
            throw new CliInputException("CombinedTuning PackedXmlDocument metadata is incomplete.");

        static string ReadString(DataModelString text)
        {
            try
            {
                return text.Value;
            }
            catch (ArgumentOutOfRangeException)
            {
                return string.Empty;
            }
        }

        var strings = stringVector.Values
            .Select(value => value is DataModelString text && text.IsValid
                ? ReadString(text)
                : throw new CliInputException("CombinedTuning contains an invalid string-table entry."))
            .ToArray();
        var textBoundary = firstElement.Row;

        string GetString(uint index)
        {
            if (index >= strings.Length)
                throw new CliInputException($"CombinedTuning string index {index} is outside the string table.");
            return strings[index];
        }

        IEnumerable<int> ReadLinkedRows(DataModelObject? firstLink)
        {
            if (firstLink is not { IsValid: true })
                yield break;
            var linkTable = firstLink.Table;
            for (var row = firstLink.Row; row < linkTable.RowCount; row++)
            {
                if (linkTable.Get(row, 0) is not DataModelObject { IsValid: true } target)
                    yield break;
                yield return target.Row;
            }
        }

        Dictionary<string, string> ReadAttributes(int nodeRow)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var firstAttributeLink = nodeTable.Get(nodeRow, "attrs") as DataModelObject;
            foreach (var attributeRow in ReadLinkedRows(firstAttributeLink))
            {
                var nameIndex = Convert.ToUInt32(attributeTable.Get(attributeRow, "name"), System.Globalization.CultureInfo.InvariantCulture);
                var valueIndex = Convert.ToUInt32(attributeTable.Get(attributeRow, "value"), System.Globalization.CultureInfo.InvariantCulture);
                var name = GetString(nameIndex);
                if (!result.TryAdd(name, GetString(valueIndex)))
                    throw new CliInputException($"CombinedTuning node has duplicate XML attribute '{name}'.");
            }
            return result;
        }

        var pending = new Stack<int>();
        var visited = new HashSet<int>();
        var matches = new List<int>();
        var sharedTables = new List<int>();
        pending.Push(topElement.Row);
        while (pending.TryPop(out var nodeRow))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nodeRow < 0 || nodeRow >= nodeTable.RowCount)
                throw new CliInputException($"CombinedTuning has an invalid node reference {nodeRow}.");
            if (!visited.Add(nodeRow))
                continue;
            if (visited.Count > MaximumExtractedElements * 4)
                throw new CliInputException("CombinedTuning node scan exceeded its safety limit.");

            var tag = GetString(Convert.ToUInt32(nodeTable.Get(nodeRow, "text"), System.Globalization.CultureInfo.InvariantCulture));
            if (nodeRow >= textBoundary && tag == "g")
            {
                if (ReadAttributes(nodeRow).GetValueOrDefault("s") == "merged")
                    sharedTables.Add(nodeRow);
            }
            if (nodeRow >= textBoundary && tag == "I")
            {
                var attributes = ReadAttributes(nodeRow);
                if (attributes.GetValueOrDefault("n") == tuningName)
                    matches.Add(nodeRow);
            }

            var childLink = nodeTable.Get(nodeRow, "children") as DataModelObject;
            foreach (var childRow in ReadLinkedRows(childLink))
                pending.Push(childRow);
        }

        if (matches.Count != 1)
            throw new CliInputException($"Expected one tuning named '{tuningName}' in CombinedTuning; found {matches.Count}.");
        if (sharedTables.Count != 1)
            throw new CliInputException($"Expected one merged shared table in CombinedTuning; found {sharedTables.Count}.");

        var definitions = new Dictionary<string, int>(StringComparer.Ordinal);
        var sharedTableChildren = nodeTable.Get(sharedTables[0], "children") as DataModelObject;
        foreach (var definitionRow in ReadLinkedRows(sharedTableChildren))
        {
            var attributes = ReadAttributes(definitionRow);
            if (!attributes.TryGetValue("x", out var index) || !definitions.TryAdd(index, definitionRow))
                throw new CliInputException("CombinedTuning merged table contains a missing or duplicate definition index.");
        }

        var elementCount = 0;
        var activeNodes = new HashSet<int>();
        var activeReferences = new HashSet<string>(StringComparer.Ordinal);
        XNode BuildNode(int nodeRow, int depth, bool removeSharedIndex = false)
        {
            if (depth > MaximumXmlDepth)
                throw new CliInputException("Extracted tuning exceeds the XML nesting safety limit.");
            if (++elementCount > MaximumExtractedElements)
                throw new CliInputException("Extracted tuning exceeds the element safety limit.");
            if (!activeNodes.Add(nodeRow))
                throw new CliInputException($"Extracted tuning contains a cyclic node reference at {nodeRow}.");

            try
            {
                var textIndex = Convert.ToUInt32(nodeTable.Get(nodeRow, "text"), System.Globalization.CultureInfo.InvariantCulture);
                var tag = GetString(textIndex);
                if (nodeRow < textBoundary)
                    return new XText(tag);

                var attributes = ReadAttributes(nodeRow);
                if (tag == "r")
                {
                    if (!attributes.TryGetValue("x", out var index) || !definitions.TryGetValue(index, out var targetRow))
                        throw new CliInputException($"Tuning '{tuningName}' contains an unresolved shared-table reference.");
                    if (!activeReferences.Add(index))
                        throw new CliInputException($"Tuning '{tuningName}' contains a cyclic shared-table reference x='{index}'.");
                    try
                    {
                        var expanded = BuildNode(targetRow, depth + 1, removeSharedIndex: true);
                        if (attributes.TryGetValue("n", out var fieldName))
                        {
                            if (expanded is not XElement expandedElement)
                                throw new CliInputException($"Tuning reference x='{index}' names a non-element value.");
                            expandedElement.SetAttributeValue("n", fieldName);
                        }
                        return expanded;
                    }
                    finally
                    {
                        activeReferences.Remove(index);
                    }
                }

                XmlConvert.VerifyName(tag);
                var element = new XElement(tag);
                foreach (var (name, value) in attributes)
                {
                    if (removeSharedIndex && name == "x")
                        continue;
                    XmlConvert.VerifyName(name);
                    element.Add(new XAttribute(name, value));
                }

                var childLink = nodeTable.Get(nodeRow, "children") as DataModelObject;
                foreach (var childRow in ReadLinkedRows(childLink))
                    element.Add(BuildNode(childRow, depth + 1));
                return element;
            }
            finally
            {
                activeNodes.Remove(nodeRow);
            }
        }

        var root = BuildNode(matches[0], 0) as XElement
            ?? throw new CliInputException("Extracted tuning root is not an XML element.");
        if (root.Name.LocalName != "I")
            throw new CliInputException($"Tuning '{tuningName}' did not resolve to an Instance tuning.");
        var instance = Fnv64.GetHash(tuningName) | 0x8000000000000000UL;
        var resourceId = new ResourceId(0x03B33DDF, 0, instance);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(fullXmlPath)!,
            $".{Path.GetFileName(fullXmlPath)}.{Guid.NewGuid():N}.tmp");

        Directory.CreateDirectory(Path.GetDirectoryName(fullXmlPath)!);
        try
        {
            var settings = new XmlWriterSettings
            {
                Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                Indent = false,
                OmitXmlDeclaration = false
            };
            await using (var output = File.Create(temporaryPath))
            using (var writer = XmlWriter.Create(output, settings))
            {
                new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(writer);
            }

            File.Move(temporaryPath, fullXmlPath);
            return new CombinedTuningExtractReport(
                fullPackagePath,
                fullXmlPath,
                resourceId.ToString(),
                tuningName,
                (string?)root.Attribute("c"),
                (string?)root.Attribute("m"),
                elementCount);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
