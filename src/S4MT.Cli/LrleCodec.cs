using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace S4MT.Cli;

// Format information adapted from the LRLE converter by CmarNYC and tau534:
// https://modthesims.info/showthread.php?t=650929
internal static class LrleCodec
{
    internal const uint ResourceTypeId = 0x2BC04EDF;
    private const int MaxPayloadBytes = 128 * 1024 * 1024;
    private const int MaxPixels = 16_777_216;
    private const int MaxSide = 16_384;
    private const int MaxPaletteColors = 65_536;
    private const int MaxWrittenMips = 9;
    private const int MaxReadMips = 15;
    private const uint Version1 = 0;
    private const uint Version2 = 0x32303056;

    private readonly record struct Mip(int Width, int Height, int Start, int Length);
    private readonly record struct Layout(uint Version, Mip[] Mips, Rgba32[] Palette);

    internal static byte[] DecodeToPng(ReadOnlySpan<byte> payload)
    {
        var layout = ReadLayout(payload);
        Rgba32[]? basePixels = null;
        for (var mipIndex = 0; mipIndex < layout.Mips.Length; mipIndex++)
        {
            var pixels = DecodeMip(payload, layout, mipIndex);
            if (mipIndex == 0)
                basePixels = pixels;
        }

        using var image = Image.LoadPixelData(basePixels!, layout.Mips[0].Width, layout.Mips[0].Height);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        if (stream.Length > MaxPayloadBytes)
            throw new InvalidDataException("LRLE decoded PNG exceeds 128 MiB.");
        return stream.ToArray();
    }

    internal static void Validate(ReadOnlySpan<byte> payload)
    {
        var layout = ReadLayout(payload);
        for (var mipIndex = 0; mipIndex < layout.Mips.Length; mipIndex++)
            _ = DecodeMip(payload, layout, mipIndex);
    }

    internal static byte[] EncodePng(ReadOnlySpan<byte> png)
    {
        if (png.Length > MaxPayloadBytes)
            throw new InvalidDataException("LRLE input PNG exceeds 128 MiB.");
        using var image = Image.Load<Rgba32>(png.ToArray());
        CheckDimensions(image.Width, image.Height);
        if (image.Width % 4 != 0 || image.Height % 4 != 0)
            throw new CliInputException("LRLE output dimensions must contain complete 4×4 tiles.");

        var rasters = new List<Rgba32[]>();
        var sizes = new List<(int Width, int Height)>();
        var width = image.Width;
        var height = image.Height;
        var pixels = new Rgba32[checked(width * height)];
        image.CopyPixelDataTo(pixels);
        while (true)
        {
            rasters.Add(pixels);
            sizes.Add((width, height));
            if (rasters.Count == MaxWrittenMips || width / 2 < 4 || height / 2 < 4 ||
                (width / 2) % 4 != 0 || (height / 2) % 4 != 0)
                break;

            var nextWidth = width / 2;
            var nextHeight = height / 2;
            var next = new Rgba32[checked(nextWidth * nextHeight)];
            for (var y = 0; y < nextHeight; y++)
                for (var x = 0; x < nextWidth; x++)
                    next[y * nextWidth + x] = pixels[(2 * y) * width + 2 * x];
            pixels = next;
            width = nextWidth;
            height = nextHeight;
        }

        var palette = new List<Rgba32>();
        var colorIndexes = new Dictionary<Rgba32, int>();
        for (var level = 0; level < rasters.Count; level++)
        {
            var raster = rasters[level];
            var mipWidth = sizes[level].Width;
            for (var position = 0; position < raster.Length; position++)
            {
                var color = raster[TileRasterIndex(position, mipWidth)];
                if (colorIndexes.ContainsKey(color) || palette.Count == MaxPaletteColors)
                    continue;
                colorIndexes.Add(color, palette.Count);
                palette.Add(color);
            }
        }

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write("LRLE"u8);
        writer.Write(Version2);
        writer.Write(checked((ushort)image.Width));
        writer.Write(checked((ushort)image.Height));
        writer.Write((uint)rasters.Count);
        var offsetTable = output.Position;
        for (var level = 0; level < rasters.Count; level++)
            writer.Write(0U);
        writer.Write((uint)palette.Count);
        foreach (var color in palette)
            WriteColor(writer, color);
        var dataStart = output.Position;
        var offsets = new uint[rasters.Count];
        for (var level = 0; level < rasters.Count; level++)
        {
            offsets[level] = checked((uint)(output.Position - dataStart));
            EncodeMip(writer, rasters[level], sizes[level].Width, colorIndexes);
            if (output.Length > MaxPayloadBytes)
                throw new CliInputException("LRLE output exceeds 128 MiB.");
        }
        output.Position = offsetTable;
        foreach (var offset in offsets)
            writer.Write(offset);
        return output.ToArray();
    }

    private static Layout ReadLayout(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadBytes)
            throw new InvalidDataException("LRLE payload exceeds 128 MiB.");
        if (payload.Length < 20 || !payload[..4].SequenceEqual("LRLE"u8))
            throw new InvalidDataException("LRLE header is missing or truncated at byte 0.");
        var version = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..8]);
        if (version is not Version1 and not Version2)
            throw new InvalidDataException($"Unsupported LRLE version 0x{version:X8} at byte 4.");
        var width = BinaryPrimitives.ReadUInt16LittleEndian(payload[8..10]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(payload[10..12]);
        CheckDimensions(width, height);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..16]);
        if (count is < 1 or > MaxReadMips)
            throw new InvalidDataException($"LRLE v{VersionLabel(version)} mip count {count} is outside 1–15 at byte 12.");
        var tableEnd = checked(16 + (int)count * 4);
        if (tableEnd > payload.Length)
            throw new InvalidDataException($"LRLE v{VersionLabel(version)} mip offset table is truncated at byte 16.");

        var offsets = new int[(int)count];
        for (var i = 0; i < offsets.Length; i++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(16 + i * 4, 4));
            if (value > MaxPayloadBytes)
                throw new InvalidDataException($"LRLE v{VersionLabel(version)} mip {i} offset is too large at byte {16 + i * 4}.");
            offsets[i] = (int)value;
            if ((i == 0 && value != 0) || (i > 0 && offsets[i] <= offsets[i - 1]))
                throw new InvalidDataException($"LRLE v{VersionLabel(version)} mip {i} offset is not increasing at byte {16 + i * 4}.");
        }

        var palette = Array.Empty<Rgba32>();
        var dataStart = tableEnd;
        if (version == Version2)
        {
            if (payload.Length - dataStart < 4)
                throw new InvalidDataException("LRLE v2 palette count is truncated.");
            var paletteCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(dataStart, 4));
            if (paletteCount > MaxPaletteColors || (long)paletteCount * 4 > payload.Length - dataStart - 4)
                throw new InvalidDataException($"LRLE v2 palette is invalid at byte {dataStart}.");
            palette = new Rgba32[(int)paletteCount];
            dataStart += 4;
            for (var i = 0; i < palette.Length; i++)
                palette[i] = ReadColor(payload, dataStart + 4 * i);
            dataStart = checked(dataStart + palette.Length * 4);
        }

        var mips = new Mip[offsets.Length];
        var mipWidth = (int)width;
        var mipHeight = (int)height;
        for (var i = 0; i < mips.Length; i++)
        {
            if (mipWidth < 4 || mipHeight < 4 || mipWidth % 4 != 0 || mipHeight % 4 != 0)
                throw new InvalidDataException($"LRLE v{VersionLabel(version)} mip {i} dimensions cannot form complete 4×4 tiles.");
            var start = checked(dataStart + offsets[i]);
            var end = i + 1 < mips.Length ? checked(dataStart + offsets[i + 1]) : payload.Length;
            if (start < dataStart || start >= end || end > payload.Length)
                throw new InvalidDataException($"LRLE v{VersionLabel(version)} mip {i} range is invalid at byte {16 + i * 4}.");
            mips[i] = new Mip(mipWidth, mipHeight, start, end - start);
            mipWidth /= 2;
            mipHeight /= 2;
        }
        return new Layout(version, mips, palette);
    }

    private static Rgba32[] DecodeMip(ReadOnlySpan<byte> payload, Layout layout, int mipIndex)
    {
        var mip = layout.Mips[mipIndex];
        var pixels = new Rgba32[checked(mip.Width * mip.Height)];
        var encoded = payload.Slice(mip.Start, mip.Length);
        var position = 0;
        var outputPosition = 0;
        try
        {
            while (position < encoded.Length)
            {
                var command = encoded[position];
                if (layout.Version == Version1)
                    DecodeV1Command(encoded, ref position, pixels, mip.Width, ref outputPosition, command);
                else
                    DecodeV2Command(encoded, ref position, pixels, mip.Width, ref outputPosition, layout.Palette, command);
            }
            if (outputPosition != pixels.Length)
                throw new InvalidDataException($"Decoded {outputPosition} pixels; expected {pixels.Length}.");
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                $"LRLE v{VersionLabel(layout.Version)} mip {mipIndex} at byte {mip.Start + position}: {exception.Message}", exception);
        }
        return pixels;
    }

    private static void DecodeV1Command(ReadOnlySpan<byte> encoded, ref int position,
        Rgba32[] raster, int width, ref int outputPosition, byte command)
    {
        switch (command & 3)
        {
            case 0:
            {
                var count = ReadLength(encoded, ref position, 2);
                CheckRun(count, raster.Length - outputPosition);
                for (var i = 0; i < count; i++)
                    PutPixel(raster, width, ref outputPosition, default);
                break;
            }
            case 1:
            {
                var count = command >> 2;
                CheckRun(count, raster.Length - outputPosition);
                position++;
                Require(encoded, position, checked(count * 4));
                for (var i = 0; i < count; i++)
                    PutPixel(raster, width, ref outputPosition, ReadColor(encoded, position + 4 * i));
                position += count * 4;
                break;
            }
            case 2:
            {
                var count = ReadLength(encoded, ref position, 2);
                CheckRun(count, raster.Length - outputPosition);
                Require(encoded, position, 4);
                var color = ReadColor(encoded, position);
                position += 4;
                for (var i = 0; i < count; i++)
                    PutPixel(raster, width, ref outputPosition, color);
                break;
            }
            case 3:
            {
                var count = command >> 2;
                CheckRun(count, raster.Length - outputPosition);
                position++;
                var channels = new byte[checked(count * 4)];
                var expanded = 0;
                while (expanded < channels.Length)
                {
                    Require(encoded, position, 1);
                    var inner = encoded[position++];
                    var amount = (inner & 1) != 0 ? (inner & 0x7f) >> 1 : (inner & 0x7f) >> 2;
                    if ((inner & 0x80) != 0)
                    {
                        Require(encoded, position, 1);
                        amount += encoded[position++] << ((inner & 1) != 0 ? 6 : 5);
                    }
                    CheckRun(amount, channels.Length - expanded);
                    if ((inner & 1) != 0)
                    {
                        Require(encoded, position, amount);
                        encoded.Slice(position, amount).CopyTo(channels.AsSpan(expanded));
                        position += amount;
                    }
                    else if ((inner & 2) != 0)
                    {
                        Require(encoded, position, 1);
                        channels.AsSpan(expanded, amount).Fill(encoded[position++]);
                    }
                    expanded += amount;
                }
                for (var i = 0; i < count; i++)
                    PutPixel(raster, width, ref outputPosition,
                        new Rgba32(channels[i + count * 2], channels[i + count],
                            channels[i], channels[i + count * 3]));
                break;
            }
        }
    }

    private static void DecodeV2Command(ReadOnlySpan<byte> encoded, ref int position,
        Rgba32[] raster, int width, ref int outputPosition, Rgba32[] palette, byte command)
    {
        if ((command & 3) == 3)
        {
            var count = ReadLength(encoded, ref position, 2);
            CheckRun(count, raster.Length - outputPosition);
            Require(encoded, position, checked(count * 4));
            for (var i = 0; i < count; i++)
                PutPixel(raster, width, ref outputPosition, ReadColor(encoded, position + i * 4));
            position += count * 4;
        }
        else if ((command & 3) == 1)
        {
            var count = ReadLength(encoded, ref position, 2);
            CheckRun(count, raster.Length - outputPosition);
            for (var i = 0; i < count; i++)
            {
                var index = ReadIndex(encoded, ref position);
                if (index >= palette.Length)
                    throw new InvalidDataException($"Palette index {index} is outside {palette.Length} colors.");
                PutPixel(raster, width, ref outputPosition, palette[index]);
            }
        }
        else if ((command & 7) is 2 or 4 or 6)
        {
            var count = ReadLength(encoded, ref position, 3);
            CheckRun(count, raster.Length - outputPosition);
            Rgba32 color;
            if ((command & 7) == 6)
            {
                Require(encoded, position, 4);
                color = ReadColor(encoded, position);
                position += 4;
            }
            else
            {
                var bytes = (command & 7) == 2 ? 1 : 2;
                Require(encoded, position, bytes);
                var index = bytes == 1 ? encoded[position] : BinaryPrimitives.ReadUInt16LittleEndian(encoded.Slice(position, 2));
                position += bytes;
                if (index >= palette.Length)
                    throw new InvalidDataException($"Palette index {index} is outside {palette.Length} colors.");
                color = palette[index];
            }
            for (var i = 0; i < count; i++)
                PutPixel(raster, width, ref outputPosition, color);
        }
        else
            throw new InvalidDataException($"Unknown v2 command 0x{command:X2}.");
    }

    private static void EncodeMip(BinaryWriter writer, Rgba32[] raster, int width,
        Dictionary<Rgba32, int> palette)
    {
        var position = 0;
        while (position < raster.Length)
        {
            var color = raster[TileRasterIndex(position, width)];
            var repeat = 1;
            while (position + repeat < raster.Length &&
                   raster[TileRasterIndex(position + repeat, width)] == color)
                repeat++;

            if (repeat >= 2)
            {
                WriteLength(writer, repeat, palette.TryGetValue(color, out var index)
                    ? index < 256 ? 2 : 4 : 6, 3);
                if (!palette.TryGetValue(color, out index))
                    WriteColor(writer, color);
                else if (index < 256)
                    writer.Write((byte)index);
                else
                    writer.Write((ushort)index);
                position += repeat;
                continue;
            }

            var copyStart = position++;
            while (position < raster.Length)
            {
                var next = raster[TileRasterIndex(position, width)];
                if (position + 1 < raster.Length &&
                    raster[TileRasterIndex(position + 1, width)] == next)
                    break;
                position++;
            }
            var count = position - copyStart;
            var indexed = true;
            for (var i = copyStart; i < position; i++)
                indexed &= palette.ContainsKey(raster[TileRasterIndex(i, width)]);
            WriteLength(writer, count, indexed ? 1 : 3, 2);
            for (var i = copyStart; i < position; i++)
            {
                var value = raster[TileRasterIndex(i, width)];
                if (indexed)
                    WriteIndex(writer, palette[value]);
                else
                    WriteColor(writer, value);
            }
        }
    }

    private static int ReadLength(ReadOnlySpan<byte> source, ref int position, int lowBitCount)
    {
        Require(source, position, 1);
        var first = source[position++];
        var value = (long)((first & 0x7f) >> lowBitCount);
        var shift = 7 - lowBitCount;
        var current = first;
        while ((current & 0x80) != 0)
        {
            Require(source, position, 1);
            current = source[position++];
            if (shift >= 35)
                throw new InvalidDataException("LRLE run length uses too many bytes.");
            value += (long)(current & 0x7f) << shift;
            if (value > MaxPixels)
                throw new InvalidDataException("LRLE run length exceeds the pixel limit.");
            shift += 7;
        }
        if (value is < 1 or > MaxPixels)
            throw new InvalidDataException("LRLE run length is invalid.");
        return (int)value;
    }

    private static int ReadIndex(ReadOnlySpan<byte> source, ref int position)
    {
        long value = 0;
        var shift = 0;
        byte current;
        do
        {
            Require(source, position, 1);
            current = source[position++];
            value += (long)(current & 0x7f) << shift;
            if (value >= MaxPaletteColors)
                throw new InvalidDataException("LRLE palette index exceeds 65,535.");
            shift += 7;
        } while ((current & 0x80) != 0 && shift < 35);
        if ((current & 0x80) != 0)
            throw new InvalidDataException("LRLE palette index uses too many bytes.");
        return (int)value;
    }

    private static void WriteLength(BinaryWriter writer, int count, int opcode, int lowBitCount)
    {
        var value = (uint)count;
        var lowMask = (1U << (7 - lowBitCount)) - 1;
        var first = (byte)(((value & lowMask) << lowBitCount) | (uint)opcode);
        value >>= 7 - lowBitCount;
        if (value != 0)
            first |= 0x80;
        writer.Write(first);
        while (value != 0)
        {
            var part = (byte)(value & 0x7f);
            value >>= 7;
            writer.Write((byte)(part | (value == 0 ? 0 : 0x80)));
        }
    }

    private static void WriteIndex(BinaryWriter writer, int index)
    {
        var value = (uint)index;
        do
        {
            var part = (byte)(value & 0x7f);
            value >>= 7;
            writer.Write((byte)(part | (value == 0 ? 0 : 0x80)));
        } while (value != 0);
    }

    private static int TileRasterIndex(int position, int width)
    {
        var tilesAcross = width / 4;
        var tile = position / 16;
        var within = position % 16;
        return ((tile / tilesAcross) * 4 + within / 4) * width +
            (tile % tilesAcross) * 4 + within % 4;
    }

    private static void PutPixel(Rgba32[] raster, int width, ref int position, Rgba32 color)
    {
        raster[TileRasterIndex(position, width)] = color;
        position++;
    }

    private static Rgba32 ReadColor(ReadOnlySpan<byte> bytes, int position) =>
        new(bytes[position + 2], bytes[position + 1], bytes[position], bytes[position + 3]);

    private static void WriteColor(BinaryWriter writer, Rgba32 color)
    {
        writer.Write(color.B);
        writer.Write(color.G);
        writer.Write(color.R);
        writer.Write(color.A);
    }

    private static void CheckDimensions(int width, int height)
    {
        if (width is < 1 or > MaxSide || height is < 1 or > MaxSide ||
            (long)width * height > MaxPixels)
            throw new InvalidDataException("LRLE dimensions exceed 16,384 per side or 16,777,216 pixels.");
    }

    private static void CheckRun(int count, int remaining)
    {
        if (count < 1 || count > remaining)
            throw new InvalidDataException($"LRLE run of {count} pixels exceeds {remaining} remaining pixels.");
    }

    private static void Require(ReadOnlySpan<byte> source, int position, int length)
    {
        if (position < 0 || length < 0 || position > source.Length - length)
            throw new InvalidDataException("LRLE command is truncated.");
    }

    private static int VersionLabel(uint version) => version == Version1 ? 1 : 2;
}
