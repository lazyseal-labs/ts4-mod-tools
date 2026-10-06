using System.Buffers.Binary;
using System.Text;

namespace S4MT.Cli;

internal static class CaspCodec
{
    private const uint SupportedVersion = 52;
    private const int MinimumHeaderLength = 14;
    private const int NameLengthOffset = 12;
    private const int NameDataOffset = 14;
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(false, false, true);

    internal static CaspDocument Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < MinimumHeaderLength)
            throw new InvalidDataException("CASP v52 payload is shorter than its verified name header.");

        var source = payload.ToArray();
        var version = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if (version != SupportedVersion)
            throw new NotSupportedException($"CASP version {version} is not supported; this codec currently supports version {SupportedVersion} only.");

        var nameByteLength = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(NameLengthOffset));
        if (nameByteLength == 0 || (nameByteLength & 1) != 0)
            throw new InvalidDataException("CASP v52 name byte length must be a positive even number.");
        if (nameByteLength > source.Length - NameDataOffset)
            throw new InvalidDataException("CASP v52 name span extends beyond the resource payload.");

        string name;
        try
        {
            name = StrictUtf16LittleEndian.GetString(source, NameDataOffset, nameByteLength);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("CASP v52 name is not valid UTF-16LE.", exception);
        }

        return new CaspDocument(source, version, name, nameByteLength);
    }

    internal static byte[] Rename(CaspDocument document, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("CASP name must not be empty or whitespace.");
        if (name.Any(character => character < ' ' || character > '~'))
            throw new InvalidDataException("CASP rename currently accepts printable ASCII characters only.");

        var encodedName = StrictUtf16LittleEndian.GetBytes(name);
        if (encodedName.Length != document.NameByteLength)
            throw new InvalidDataException(
                $"CASP v52 name must keep its encoded byte length ({document.NameByteLength}); variable-length renames are not supported.");

        var renamedPayload = document.CopySourcePayload();
        encodedName.CopyTo(renamedPayload, NameDataOffset);
        return renamedPayload;
    }
}

internal sealed class CaspDocument
{
    private readonly byte[] sourcePayload;

    internal CaspDocument(byte[] sourcePayload, uint version, string name, int nameByteLength)
    {
        this.sourcePayload = sourcePayload;
        Version = version;
        Name = name;
        NameByteLength = nameByteLength;
    }

    internal uint Version { get; }
    internal string Name { get; }
    internal int NameByteLength { get; }
    internal int PayloadLength => sourcePayload.Length;

    internal byte[] CopySourcePayload() => (byte[])sourcePayload.Clone();
}
