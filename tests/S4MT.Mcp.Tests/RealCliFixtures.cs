using System.Text;
using LlamaLogic.Packages.Models.Data;

namespace S4MT.Mcp.Tests;

internal static class RealCliFixtures
{
    internal static byte[] Casp()
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(52u);
        writer.Write(0ul);
        writer.Write((ushort)8);
        writer.Write(Encoding.Unicode.GetBytes("Test"));
        writer.Write(new byte[] { 9, 8, 7, 6 });
        return bytes.ToArray();
    }

    internal static byte[] SimData()
    {
        var model = new DataModel { Version = DataModelVersion.SmartSimOriginalEdition };
        var table = new DataModelTable("Values", "Values", 1);
        table.AddColumn("Count", DataModelType.INT32, 0, new object[] { 7 });
        model.Tables.Add(table);
        return model.Encode().ToArray();
    }

    internal static byte[] CombinedTuning()
    {
        var model = new DataModel { Version = DataModelVersion.SmartSimOriginalEdition };
        var document = new DataModelTable("Document", "PackedXmlDocument", 1);
        var nodes = new DataModelTable("Nodes", "PackedXmlNode", 2);
        var attributes = new DataModelTable("Attributes", "PackedXmlAttribute", 3);
        var links = new DataModelTable(null, null, null);
        var attributeLinks = new DataModelTable(null, null, null);
        var stringReferences = new DataModelTable(null, null, null);
        var characters = new DataModelTable(null, null, null);
        foreach (var table in new[] { document, nodes, attributes, links, attributeLinks, stringReferences, characters }) model.Tables.Add(table);
        var strings = new[] { "combined", "g", "I", "s", "merged", "n", "fixture", "c", "Test", "i", "snippet", "m", "tests", "1" };
        var chars = new List<object>();
        var offsets = new List<int>();
        foreach (var text in strings)
        {
            var offset = chars.Count;
            chars.AddRange((text + '\0').Select(character => (object)character));
            offsets.Add(offset);
        }
        characters.AddColumn(null, DataModelType.CHAR8, 0, chars);
        var refs = offsets.Select(offset => (object)new DataModelString(characters, offset)).ToArray();
        stringReferences.AddColumn(null, DataModelType.STRING8, 0, refs);
        nodes.AddColumn("text", DataModelType.UINT32, 0, new object[] { 0u, 1u, 2u });
        nodes.AddColumn("children", DataModelType.OBJECT, 0, Enumerable.Repeat<object>(DataModelObject.NullReference, 3));
        nodes.AddColumn("attrs", DataModelType.OBJECT, 0, Enumerable.Repeat<object>(DataModelObject.NullReference, 3));
        attributes.AddColumn("name", DataModelType.UINT32, 0, new object[] { 3u, 5u, 7u, 9u, 11u, 3u });
        attributes.AddColumn("value", DataModelType.UINT32, 0, new object[] { 4u, 6u, 8u, 10u, 12u, 13u });
        DataModelObject Node(int row) => new(nodes, row, 0, DataModelType.OBJECT);
        DataModelObject Attribute(int row) => new(attributes, row, 0, DataModelType.OBJECT);
        links.AddColumn(null, DataModelType.OBJECT, 0, new object[] { Node(1), Node(2), DataModelObject.NullReference });
        attributeLinks.AddColumn(null, DataModelType.OBJECT, 0, new object[]
        {
            Attribute(0), DataModelObject.NullReference, Attribute(1), Attribute(2), Attribute(3), Attribute(4), Attribute(5), DataModelObject.NullReference
        });
        nodes.Set(0, "children", new DataModelObject(links, 0, 0, DataModelType.OBJECT));
        nodes.Set(1, "attrs", new DataModelObject(attributeLinks, 0, 0, DataModelType.OBJECT));
        nodes.Set(2, "attrs", new DataModelObject(attributeLinks, 2, 0, DataModelType.OBJECT));
        document.AddColumn("first_element", DataModelType.OBJECT, 0, new object[] { Node(0) });
        document.AddColumn("top_element", DataModelType.OBJECT, 0, new object[] { Node(0) });
        document.AddColumn("string_table", DataModelType.VECTOR, 0, new object[] { new DataModelVector(stringReferences, 0, DataModelType.STRING8, refs.Length) });
        return model.Encode().ToArray();
    }
}
