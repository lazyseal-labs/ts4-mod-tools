using System.Buffers.Binary;
using System.Text;
using LlamaLogic.Packages;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace S4MT.Cli;

internal static class RleTextureCodec
{
    internal const uint Rle2ResourceTypeId = 0x3453CF95;
    internal const uint RlesResourceTypeId = 0xBA856C78;

    private const int HeaderSize = 16;
    private const int MaxPayloadBytes = 128 * 1024 * 1024;
    private const int MaxDimension = 16_384;
    private const long MaxPixels = 16_777_216;
    private const int MaxMipCount = 15;

    internal static bool Supports(ResourceId id) =>
        id.Type is Rle2ResourceTypeId or RlesResourceTypeId;

    internal static bool IsRle2(ResourceId id) => id.Type == Rle2ResourceTypeId;

    internal sealed record Rle2MipExport(int MipLevel, int MipCount, int Width, int Height, byte[] PngBytes);

    internal sealed record RlesMipPngExport(
        int MipLevel,
        int MipCount,
        int BaseWidth,
        int BaseHeight,
        int Width,
        int Height,
        byte[] ColorPngBytes,
        byte[] MaskPngBytes);

    internal sealed record RlesEncodedPayload(byte[] Payload, int Width, int Height, int MipCount);

    internal static void Validate(ResourceId id, ReadOnlySpan<byte> payload)
    {
        var profile = id.Type switch
        {
            Rle2ResourceTypeId => new FormatProfile("RLE2", 5),
            RlesResourceTypeId => new FormatProfile("RLES", 6),
            _ => throw new InvalidDataException($"Resource type 0x{id.Type:X8} is not a supported RLE texture.")
        };

        _ = Parse(id, profile, payload);
    }

    internal static Rle2MipExport DecodeRle2MipToPng(ResourceId id, ReadOnlySpan<byte> payload, int mipLevel)
    {
        if (!IsRle2(id))
            throw new InvalidDataException($"Resource {id} is not an RLE2 image.");
        if (mipLevel < 0)
            throw new InvalidDataException($"RLE2 resource {id} mip level must not be negative.");

        var layout = Parse(id, new FormatProfile("RLE2", 5), payload);
        if (mipLevel >= layout.Mips.Length)
            throw new InvalidDataException(
                $"RLE2 resource {id} mip level {mipLevel} is outside the available range 0–{layout.Mips.Length - 1}.");

        var mip = layout.Mips[mipLevel];
        var pixels = new Rgba32[checked(mip.Width * mip.Height)];
        DecodeRle2Mip(payload, mip, pixels);

        using var image = Image.LoadPixelData(pixels, mip.Width, mip.Height);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        if (stream.Length > MaxPayloadBytes)
            throw new InvalidDataException($"RLE2 resource {id} decoded PNG exceeds 128 MiB.");

        return new Rle2MipExport(mipLevel, layout.Mips.Length, mip.Width, mip.Height, stream.ToArray());
    }

    internal static RlesMipPngExport DecodeRlesMipToPngs(ResourceId id, ReadOnlySpan<byte> payload, int mipLevel)
    {
        if (id.Type != RlesResourceTypeId)
            throw new InvalidDataException($"RLES resource {id}: expected resource type 0x{RlesResourceTypeId:X8}.");

        try
        {
            if (mipLevel < 0)
                throw Invalid("mip level must not be negative.");

            var layout = ParsePayload(new FormatProfile("RLES", 6), payload);
            if (mipLevel >= layout.Mips.Length)
                throw Invalid($"mip level {mipLevel} is outside the available range 0–{layout.Mips.Length - 1}.");

            var mip = layout.Mips[mipLevel];
            var pixelCount = checked(mip.Width * mip.Height);
            var colorPixels = new Rgba32[pixelCount];
            var maskPixels = new Rgba32[pixelCount];
            DecodeRlesMip(payload, mip, colorPixels, maskPixels);

            using var colorImage = Image.LoadPixelData(colorPixels, mip.Width, mip.Height);
            using var colorStream = new MemoryStream();
            colorImage.SaveAsPng(colorStream);
            if (colorStream.Length > MaxPayloadBytes)
                throw Invalid("decoded color PNG exceeds 128 MiB.");

            using var maskImage = Image.LoadPixelData(maskPixels, mip.Width, mip.Height);
            using var maskStream = new MemoryStream();
            maskImage.SaveAsPng(maskStream);
            if (maskStream.Length > MaxPayloadBytes)
                throw Invalid("decoded mask PNG exceeds 128 MiB.");

            var baseMip = layout.Mips[0];
            return new RlesMipPngExport(
                mipLevel,
                layout.Mips.Length,
                baseMip.Width,
                baseMip.Height,
                mip.Width,
                mip.Height,
                colorStream.ToArray(),
                maskStream.ToArray());
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"RLES resource {id}: {exception.Message}", exception);
        }
        catch (ImageFormatException exception)
        {
            throw new InvalidDataException($"RLES resource {id}: decoded PNG could not be encoded: {exception.Message}", exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"RLES resource {id}: integer overflow while decoding.", exception);
        }
    }

    internal static byte[] EncodeRle2Png(ResourceId id, ReadOnlySpan<byte> png)
    {
        if (!IsRle2(id))
            throw new InvalidDataException($"Resource {id} is not an RLE2 image.");
        if (png.Length > MaxPayloadBytes)
            throw new InvalidDataException($"RLE2 input PNG for {id} exceeds 128 MiB.");

        try
        {
            using var image = Image.Load<Rgba32>(png.ToArray());
            if (image.Width is 0 or > MaxDimension || image.Height is 0 or > MaxDimension ||
                (long)image.Width * image.Height > MaxPixels)
                throw Invalid($"input dimensions {image.Width}x{image.Height} exceed the supported limits.");

            var baseWidth = image.Width;
            var baseHeight = image.Height;
            var mipCount = GetFullMipCount(image.Width, image.Height);
            using var commands = new MemoryStream();
            using var colorsLow = new MemoryStream();
            using var colorsHigh = new MemoryStream();
            using var alphaEndpoints = new MemoryStream();
            using var alphaIndices = new MemoryStream();
            var streams = new[] { commands, colorsLow, colorsHigh, alphaEndpoints, alphaIndices };
            var mipOffsets = new int[mipCount, streams.Length];
            Span<byte> block = stackalloc byte[16];
            Span<byte> commandBytes = stackalloc byte[sizeof(ushort)];
            Span<Rgba32> pixels = stackalloc Rgba32[16];
            var width = image.Width;
            var height = image.Height;

            for (var mip = 0; mip < mipCount; mip++)
            {
                for (var stream = 0; stream < streams.Length; stream++)
                    mipOffsets[mip, stream] = checked((int)streams[stream].Length);

                var blocksWide = checked((width + 3) / 4);
                var blocksHigh = checked((height + 3) / 4);
                var blockCount = checked(blocksWide * blocksHigh);
                WriteOp1Commands(commands, blockCount, commandBytes);

                for (var blockY = 0; blockY < blocksHigh; blockY++)
                {
                    for (var blockX = 0; blockX < blocksWide; blockX++)
                    {
                        for (var localY = 0; localY < 4; localY++)
                        {
                            for (var localX = 0; localX < 4; localX++)
                            {
                                var x = Math.Min(blockX * 4 + localX, width - 1);
                                var y = Math.Min(blockY * 4 + localY, height - 1);
                                pixels[localY * 4 + localX] = image[x, y];
                            }
                        }

                        EncodeBc3Block(pixels, block);
                        colorsLow.Write(block.Slice(8, 4));
                        colorsHigh.Write(block.Slice(12, 4));
                        alphaEndpoints.Write(block[..2]);
                        alphaIndices.Write(block.Slice(2, 6));
                    }
                }

                if (mip + 1 < mipCount)
                {
                    width = Math.Max(1, width / 2);
                    height = Math.Max(1, height / 2);
                    image.Mutate(context => context.Resize(width, height, KnownResamplers.Box));
                }
            }

            var tableEnd = checked(HeaderSize + mipCount * streams.Length * sizeof(uint));
            long payloadSize = tableEnd;
            foreach (var stream in streams)
                payloadSize = checked(payloadSize + stream.Length);
            if (payloadSize > MaxPayloadBytes)
                throw Invalid("encoded RLE2 payload exceeds 128 MiB.");

            using var output = new MemoryStream(checked((int)payloadSize));
            Span<byte> header = stackalloc byte[HeaderSize];
            "DXT5RLE2"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header[8..], checked((ushort)baseWidth));
            BinaryPrimitives.WriteUInt16LittleEndian(header[10..], checked((ushort)baseHeight));
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], checked((ushort)mipCount));
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 0);
            output.Write(header);

            Span<byte> offsetBytes = stackalloc byte[sizeof(uint)];
            for (var mip = 0; mip < mipCount; mip++)
            {
                for (var stream = 0; stream < streams.Length; stream++)
                {
                    long offset = tableEnd + mipOffsets[mip, stream];
                    for (var precedingStream = 0; precedingStream < stream; precedingStream++)
                        offset = checked(offset + streams[precedingStream].Length);
                    if (offset > uint.MaxValue)
                        throw Invalid("encoded RLE2 stream offset exceeds the 32-bit format limit.");
                    BinaryPrimitives.WriteUInt32LittleEndian(offsetBytes, checked((uint)offset));
                    output.Write(offsetBytes);
                }
            }

            foreach (var stream in streams)
                output.Write(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
            var result = output.ToArray();
            _ = ParsePayload(new FormatProfile("RLE2", 5), result);
            return result;
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"RLE2 resource {id}: {exception.Message}", exception);
        }
        catch (ImageFormatException exception)
        {
            throw new InvalidDataException($"RLE2 resource {id}: input PNG could not be decoded: {exception.Message}", exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"RLE2 resource {id}: integer overflow while encoding.", exception);
        }
    }

    internal static RlesEncodedPayload EncodeRlesPngs(
        ResourceId id,
        ReadOnlySpan<byte> colorPng,
        ReadOnlySpan<byte> maskPng)
    {
        if (id.Type != RlesResourceTypeId)
            throw new InvalidDataException($"Resource {id} is not an RLES image.");
        if (colorPng.Length > MaxPayloadBytes)
            throw new InvalidDataException($"RLES color input PNG for {id} exceeds 128 MiB.");
        if (maskPng.Length > MaxPayloadBytes)
            throw new InvalidDataException($"RLES mask input PNG for {id} exceeds 128 MiB.");

        try
        {
            using var colorImage = Image.Load<Rgba32>(colorPng.ToArray());
            using var maskImage = Image.Load<Rgba32>(maskPng.ToArray());
            ValidateImageDimensions(colorImage.Width, colorImage.Height, "color");
            ValidateImageDimensions(maskImage.Width, maskImage.Height, "mask");
            if (colorImage.Width != maskImage.Width || colorImage.Height != maskImage.Height)
                throw Invalid(
                    $"color dimensions {colorImage.Width}x{colorImage.Height} do not match mask dimensions {maskImage.Width}x{maskImage.Height}.");

            for (var y = 0; y < maskImage.Height; y++)
            {
                for (var x = 0; x < maskImage.Width; x++)
                {
                    var pixel = maskImage[x, y];
                    if (pixel.R != pixel.G || pixel.R != pixel.B)
                        throw Invalid($"mask pixel at ({x},{y}) is not grayscale.");
                    if (pixel.A != byte.MaxValue)
                        throw Invalid($"mask pixel at ({x},{y}) is not opaque.");
                }
            }

            var baseWidth = colorImage.Width;
            var baseHeight = colorImage.Height;
            var mipCount = GetFullMipCount(baseWidth, baseHeight);
            using var commands = new MemoryStream();
            using var colorsLow = new MemoryStream();
            using var colorsHigh = new MemoryStream();
            using var alphaEndpoints = new MemoryStream();
            using var alphaIndices = new MemoryStream();
            using var mask = new MemoryStream();
            var streams = new[] { commands, colorsLow, colorsHigh, alphaEndpoints, alphaIndices, mask };
            var mipOffsets = new int[mipCount, streams.Length];
            Span<byte> block = stackalloc byte[16];
            Span<byte> maskBlock = stackalloc byte[16];
            Span<byte> commandBytes = stackalloc byte[sizeof(ushort)];
            Span<Rgba32> pixels = stackalloc Rgba32[16];
            var width = baseWidth;
            var height = baseHeight;

            for (var mip = 0; mip < mipCount; mip++)
            {
                for (var stream = 0; stream < streams.Length; stream++)
                    mipOffsets[mip, stream] = checked((int)streams[stream].Length);

                var blocksWide = checked((width + 3) / 4);
                var blocksHigh = checked((height + 3) / 4);
                var blockCount = checked(blocksWide * blocksHigh);
                WriteOp1Commands(commands, blockCount, commandBytes);

                for (var blockY = 0; blockY < blocksHigh; blockY++)
                {
                    for (var blockX = 0; blockX < blocksWide; blockX++)
                    {
                        for (var localY = 0; localY < 4; localY++)
                        {
                            for (var localX = 0; localX < 4; localX++)
                            {
                                var x = Math.Min(blockX * 4 + localX, width - 1);
                                var y = Math.Min(blockY * 4 + localY, height - 1);
                                var localIndex = localY * 4 + localX;
                                pixels[localIndex] = colorImage[x, y];
                                maskBlock[localIndex] = maskImage[x, y].R;
                            }
                        }

                        EncodeBc3Block(pixels, block);
                        colorsLow.Write(block.Slice(8, 4));
                        colorsHigh.Write(block.Slice(12, 4));
                        alphaEndpoints.Write(block[..2]);
                        alphaIndices.Write(block.Slice(2, 6));
                        mask.Write(maskBlock);
                    }
                }

                if (mip + 1 < mipCount)
                {
                    width = Math.Max(1, width / 2);
                    height = Math.Max(1, height / 2);
                    colorImage.Mutate(context => context.Resize(width, height, KnownResamplers.Box));
                    maskImage.Mutate(context => context.Resize(width, height, KnownResamplers.Box));
                }
            }

            var tableEnd = checked(HeaderSize + mipCount * streams.Length * sizeof(uint));
            long payloadSize = tableEnd;
            foreach (var stream in streams)
                payloadSize = checked(payloadSize + stream.Length);
            if (payloadSize > MaxPayloadBytes)
                throw Invalid("encoded RLES payload exceeds 128 MiB.");

            using var output = new MemoryStream(checked((int)payloadSize));
            Span<byte> header = stackalloc byte[HeaderSize];
            "DXT5RLES"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header[8..], checked((ushort)baseWidth));
            BinaryPrimitives.WriteUInt16LittleEndian(header[10..], checked((ushort)baseHeight));
            BinaryPrimitives.WriteUInt16LittleEndian(header[12..], checked((ushort)mipCount));
            BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 0);
            output.Write(header);

            Span<byte> offsetBytes = stackalloc byte[sizeof(uint)];
            for (var mip = 0; mip < mipCount; mip++)
            {
                for (var stream = 0; stream < streams.Length; stream++)
                {
                    long offset = tableEnd + mipOffsets[mip, stream];
                    for (var precedingStream = 0; precedingStream < stream; precedingStream++)
                        offset = checked(offset + streams[precedingStream].Length);
                    if (offset > uint.MaxValue)
                        throw Invalid("encoded RLES stream offset exceeds the 32-bit format limit.");
                    BinaryPrimitives.WriteUInt32LittleEndian(offsetBytes, checked((uint)offset));
                    output.Write(offsetBytes);
                }
            }

            foreach (var stream in streams)
                output.Write(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));

            var result = output.ToArray();
            _ = ParsePayload(new FormatProfile("RLES", 6), result);
            return new RlesEncodedPayload(result, baseWidth, baseHeight, mipCount);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"RLES resource {id}: {exception.Message}", exception);
        }
        catch (ImageFormatException exception)
        {
            throw new InvalidDataException($"RLES resource {id}: input PNG could not be decoded: {exception.Message}", exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"RLES resource {id}: integer overflow while encoding.", exception);
        }
    }

    private static void ValidateImageDimensions(int width, int height, string plane)
    {
        if (width is 0 or > MaxDimension || height is 0 or > MaxDimension ||
            (long)width * height > MaxPixels)
            throw Invalid($"{plane} input dimensions {width}x{height} exceed the supported limits.");
    }

    private static void WriteOp1Commands(MemoryStream commands, int blockCount, Span<byte> encodedCommand)
    {
        var remaining = blockCount;
        while (remaining > 0)
        {
            var count = Math.Min(remaining, 0x3FFF);
            var command = checked((ushort)((count << 2) | 1));
            BinaryPrimitives.WriteUInt16LittleEndian(encodedCommand, command);
            commands.Write(encodedCommand);
            remaining -= count;
        }
    }

    private static void EncodeBc3Block(ReadOnlySpan<Rgba32> pixels, Span<byte> block)
    {
        EncodeAlphaBlock(pixels, block);
        EncodeColorBlock(pixels, block);
    }

    private static void EncodeAlphaBlock(ReadOnlySpan<Rgba32> pixels, Span<byte> block)
    {
        var minimumAlpha = byte.MaxValue;
        var maximumAlpha = byte.MinValue;
        for (var index = 0; index < pixels.Length; index++)
        {
            minimumAlpha = Math.Min(minimumAlpha, pixels[index].A);
            maximumAlpha = Math.Max(maximumAlpha, pixels[index].A);
        }
        var alpha0 = maximumAlpha;
        var alpha1 = minimumAlpha;
        NormalizeAlphaEndpoints(ref alpha0, ref alpha1);

        Span<byte> palette = stackalloc byte[8];
        Span<byte> indices = stackalloc byte[16];
        var bestError = long.MaxValue;
        var bestAlpha0 = alpha0;
        var bestAlpha1 = alpha1;
        for (var iteration = 0; iteration < 8; iteration++)
        {
            BuildAlphaPalette(alpha0, alpha1, palette);
            long error = 0;
            for (var pixel = 0; pixel < pixels.Length; pixel++)
            {
                indices[pixel] = FindAlphaIndex(pixels[pixel].A, palette);
                var delta = pixels[pixel].A - palette[indices[pixel]];
                error += delta * delta;
            }
            if (error < bestError)
            {
                bestError = error;
                bestAlpha0 = alpha0;
                bestAlpha1 = alpha1;
            }
            if (FitAlphaEndpoints(pixels, indices, out var fitted0, out var fitted1))
            {
                NormalizeAlphaEndpoints(ref fitted0, ref fitted1);
                alpha0 = fitted0;
                alpha1 = fitted1;
            }
        }

        alpha0 = bestAlpha0;
        alpha1 = bestAlpha1;
        BuildAlphaPalette(alpha0, alpha1, palette);
        block[0] = alpha0;
        block[1] = alpha1;
        ulong packedIndices = 0;
        for (var pixel = 0; pixel < pixels.Length; pixel++)
        {
            var index = FindAlphaIndex(pixels[pixel].A, palette);
            packedIndices |= (ulong)index << (pixel * 3);
        }
        for (var index = 0; index < 6; index++)
            block[2 + index] = (byte)(packedIndices >> (index * 8));
    }

    private static bool FitAlphaEndpoints(
        ReadOnlySpan<Rgba32> pixels,
        ReadOnlySpan<byte> indices,
        out byte alpha0,
        out byte alpha1)
    {
        double firstSquared = 0;
        double mixed = 0;
        double secondSquared = 0;
        double firstValue = 0;
        double secondValue = 0;
        for (var pixel = 0; pixel < pixels.Length; pixel++)
        {
            var (firstWeight, secondWeight) = GetAlphaWeights(indices[pixel]);
            firstSquared += firstWeight * firstWeight;
            mixed += firstWeight * secondWeight;
            secondSquared += secondWeight * secondWeight;
            firstValue += firstWeight * pixels[pixel].A;
            secondValue += secondWeight * pixels[pixel].A;
        }

        var determinant = firstSquared * secondSquared - mixed * mixed;
        if (determinant <= 1e-9)
        {
            alpha0 = default;
            alpha1 = default;
            return false;
        }

        alpha0 = ClampToByte((firstValue * secondSquared - secondValue * mixed) / determinant);
        alpha1 = ClampToByte((secondValue * firstSquared - firstValue * mixed) / determinant);
        return true;
    }

    private static (double First, double Second) GetAlphaWeights(int index) => index switch
    {
        0 => (1, 0),
        1 => (0, 1),
        _ => ((8 - index) / 7d, (index - 1) / 7d)
    };

    private static byte FindAlphaIndex(byte value, ReadOnlySpan<byte> palette)
    {
        var bestIndex = 0;
        var bestDelta = int.MaxValue;
        for (var candidate = 0; candidate < palette.Length; candidate++)
        {
            var delta = Math.Abs(value - palette[candidate]);
            if (delta < bestDelta)
            {
                bestDelta = delta;
                bestIndex = candidate;
            }
        }
        return checked((byte)bestIndex);
    }

    private static void BuildAlphaPalette(byte alpha0, byte alpha1, Span<byte> palette)
    {
        palette[0] = alpha0;
        palette[1] = alpha1;
        for (var index = 1; index <= 6; index++)
            palette[index + 1] = (byte)(((7 - index) * alpha0 + index * alpha1) / 7);
    }

    private static void NormalizeAlphaEndpoints(ref byte alpha0, ref byte alpha1)
    {
        if (alpha0 < alpha1)
            (alpha0, alpha1) = (alpha1, alpha0);
        if (alpha0 == alpha1)
        {
            if (alpha0 == 0)
                alpha0 = 1;
            else
                alpha1--;
        }
    }

    private static void EncodeColorBlock(ReadOnlySpan<Rgba32> pixels, Span<byte> block)
    {
        var first = 0;
        var second = 0;
        var greatestDistance = -1;
        for (var left = 0; left < pixels.Length; left++)
        {
            for (var right = left + 1; right < pixels.Length; right++)
            {
                var red = pixels[left].R - pixels[right].R;
                var green = pixels[left].G - pixels[right].G;
                var blue = pixels[left].B - pixels[right].B;
                var distance = red * red + green * green + blue * blue;
                if (distance > greatestDistance)
                {
                    greatestDistance = distance;
                    first = left;
                    second = right;
                }
            }
        }

        var color0 = PackRgb565(pixels[first]);
        var color1 = PackRgb565(pixels[second]);
        NormalizeColorEndpoints(ref color0, ref color1);

        Span<Rgba32> palette = stackalloc Rgba32[4];
        Span<byte> indices = stackalloc byte[16];
        var bestError = long.MaxValue;
        var bestColor0 = color0;
        var bestColor1 = color1;
        for (var iteration = 0; iteration < 8; iteration++)
        {
            BuildColorPalette(color0, color1, palette);
            long error = 0;
            for (var pixel = 0; pixel < pixels.Length; pixel++)
            {
                indices[pixel] = FindColorIndex(pixels[pixel], palette);
                var selected = palette[indices[pixel]];
                var red = pixels[pixel].R - selected.R;
                var green = pixels[pixel].G - selected.G;
                var blue = pixels[pixel].B - selected.B;
                error += red * red + green * green + blue * blue;
            }
            if (error < bestError)
            {
                bestError = error;
                bestColor0 = color0;
                bestColor1 = color1;
            }
            if (FitColorEndpoints(pixels, indices, out var fitted0, out var fitted1))
            {
                color0 = PackRgb565(fitted0);
                color1 = PackRgb565(fitted1);
                NormalizeColorEndpoints(ref color0, ref color1);
            }
        }

        color0 = bestColor0;
        color1 = bestColor1;
        BinaryPrimitives.WriteUInt16LittleEndian(block.Slice(8, 2), color0);
        BinaryPrimitives.WriteUInt16LittleEndian(block.Slice(10, 2), color1);
        BuildColorPalette(color0, color1, palette);
        uint packedIndices = 0;
        for (var pixel = 0; pixel < pixels.Length; pixel++)
            packedIndices |= (uint)FindColorIndex(pixels[pixel], palette) << (pixel * 2);
        BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(12, 4), packedIndices);
    }

    private static bool FitColorEndpoints(
        ReadOnlySpan<Rgba32> pixels,
        ReadOnlySpan<byte> indices,
        out Rgba32 endpoint0,
        out Rgba32 endpoint1)
    {
        double firstSquared = 0;
        double mixed = 0;
        double secondSquared = 0;
        double firstRed = 0;
        double firstGreen = 0;
        double firstBlue = 0;
        double secondRed = 0;
        double secondGreen = 0;
        double secondBlue = 0;
        for (var pixel = 0; pixel < pixels.Length; pixel++)
        {
            var (firstWeight, secondWeight) = GetColorWeights(indices[pixel]);
            firstSquared += firstWeight * firstWeight;
            mixed += firstWeight * secondWeight;
            secondSquared += secondWeight * secondWeight;
            firstRed += firstWeight * pixels[pixel].R;
            firstGreen += firstWeight * pixels[pixel].G;
            firstBlue += firstWeight * pixels[pixel].B;
            secondRed += secondWeight * pixels[pixel].R;
            secondGreen += secondWeight * pixels[pixel].G;
            secondBlue += secondWeight * pixels[pixel].B;
        }

        var determinant = firstSquared * secondSquared - mixed * mixed;
        if (determinant <= 1e-9)
        {
            endpoint0 = default;
            endpoint1 = default;
            return false;
        }

        endpoint0 = new Rgba32(
            SolveEndpoint(firstRed, secondRed, firstSquared, mixed, secondSquared, determinant),
            SolveEndpoint(firstGreen, secondGreen, firstSquared, mixed, secondSquared, determinant),
            SolveEndpoint(firstBlue, secondBlue, firstSquared, mixed, secondSquared, determinant),
            255);
        endpoint1 = new Rgba32(
            SolveEndpoint(secondRed, firstRed, secondSquared, mixed, firstSquared, determinant),
            SolveEndpoint(secondGreen, firstGreen, secondSquared, mixed, firstSquared, determinant),
            SolveEndpoint(secondBlue, firstBlue, secondSquared, mixed, firstSquared, determinant),
            255);
        return true;
    }

    private static (double First, double Second) GetColorWeights(int index) => index switch
    {
        0 => (1, 0),
        1 => (0, 1),
        2 => (2d / 3, 1d / 3),
        _ => (1d / 3, 2d / 3)
    };

    private static byte SolveEndpoint(
        double firstValue,
        double secondValue,
        double firstSquared,
        double mixed,
        double secondSquared,
        double determinant) =>
        ClampToByte((firstValue * secondSquared - secondValue * mixed) / determinant);

    private static byte ClampToByte(double value) => checked((byte)Math.Clamp((int)Math.Round(value), 0, 255));

    private static byte FindColorIndex(Rgba32 pixel, ReadOnlySpan<Rgba32> palette)
    {
        var bestIndex = 0;
        var bestDistance = int.MaxValue;
        for (var candidate = 0; candidate < palette.Length; candidate++)
        {
            var red = pixel.R - palette[candidate].R;
            var green = pixel.G - palette[candidate].G;
            var blue = pixel.B - palette[candidate].B;
            var distance = red * red + green * green + blue * blue;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = candidate;
            }
        }
        return checked((byte)bestIndex);
    }

    private static void BuildColorPalette(ushort color0, ushort color1, Span<Rgba32> palette)
    {
        var endpoint0 = ReadRgb565(color0);
        var endpoint1 = ReadRgb565(color1);
        palette[0] = endpoint0;
        palette[1] = endpoint1;
        palette[2] = Interpolate(endpoint0, endpoint1, 2, 1);
        palette[3] = Interpolate(endpoint0, endpoint1, 1, 2);
    }

    private static void NormalizeColorEndpoints(ref ushort color0, ref ushort color1)
    {
        if (color0 < color1)
            (color0, color1) = (color1, color0);
        if (color0 == color1)
        {
            if (color0 < ushort.MaxValue)
                color0++;
            else
                color1--;
        }
    }

    private static ushort PackRgb565(Rgba32 pixel)
    {
        var red = (pixel.R * 31 + 127) / 255;
        var green = (pixel.G * 63 + 127) / 255;
        var blue = (pixel.B * 31 + 127) / 255;
        return checked((ushort)((red << 11) | (green << 5) | blue));
    }

    private static RleTextureLayout Parse(ResourceId id, FormatProfile profile, ReadOnlySpan<byte> payload)
    {
        try
        {
            return ParsePayload(profile, payload);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"{profile.Name} resource {id}: {exception.Message}", exception);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"{profile.Name} resource {id}: integer overflow in layout.", exception);
        }
    }

    private static RleTextureLayout ParsePayload(FormatProfile profile, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderSize)
            throw Invalid("header is truncated.");
        if (payload.Length > MaxPayloadBytes)
            throw Invalid("payload exceeds 128 MiB.");

        if (!payload[..4].SequenceEqual("DXT5"u8))
            throw Invalid("pixel format must be DXT5.");
        if (!payload.Slice(4, 4).SequenceEqual(Encoding.ASCII.GetBytes(profile.Name)))
            throw Invalid($"resource tag must be {profile.Name}.");

        var width = BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(payload[10..]);
        var mipCount = BinaryPrimitives.ReadUInt16LittleEndian(payload[12..]);
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(payload[14..]);
        if (reserved != 0)
            throw Invalid($"reserved header field must be zero, got {reserved}.");
        if (width is 0 or > MaxDimension || height is 0 or > MaxDimension)
            throw Invalid($"dimensions {width}x{height} exceed the supported 1–{MaxDimension} range.");
        if ((long)width * height > MaxPixels)
            throw Invalid($"dimensions {width}x{height} exceed the {MaxPixels} pixel limit.");

        var maximumMipCount = GetFullMipCount(width, height);
        if (mipCount is 0 or > MaxMipCount || mipCount > maximumMipCount)
            throw Invalid($"mip count {mipCount} is outside the supported 1–{maximumMipCount} range.");

        var tableBytes = checked((long)mipCount * profile.OffsetColumns * sizeof(uint));
        var tableEnd = checked(HeaderSize + tableBytes);
        if (tableEnd > payload.Length)
            throw Invalid("mip offset table is truncated.");

        var offsets = new uint[mipCount, profile.OffsetColumns];
        for (var mip = 0; mip < mipCount; mip++)
        {
            for (var column = 0; column < profile.OffsetColumns; column++)
            {
                var position = checked(HeaderSize + (mip * profile.OffsetColumns + column) * sizeof(uint));
                offsets[mip, column] = BinaryPrimitives.ReadUInt32LittleEndian(payload[position..]);
            }
        }

        var streamStarts = new int[profile.OffsetColumns];
        var streamEnds = new int[profile.OffsetColumns];
        for (var column = 0; column < profile.OffsetColumns; column++)
        {
            var start = (long)offsets[0, column];
            var end = column + 1 < profile.OffsetColumns
                ? (long)offsets[0, column + 1]
                : payload.Length;
            if (start < tableEnd || start > end || end > payload.Length)
                throw Invalid($"stream {column} has an invalid overall range [{start}, {end}).");
            streamStarts[column] = checked((int)start);
            streamEnds[column] = checked((int)end);

            var previous = start;
            for (var mip = 0; mip < mipCount; mip++)
            {
                var offset = (long)offsets[mip, column];
                if (offset < start || offset > end || (mip == 0 && offset != start) || offset < previous)
                    throw Invalid($"mip {mip} stream {column} offset {offset} is outside or out of order.");
                previous = offset;
            }
        }

        var mipWidth = (int)width;
        var mipHeight = (int)height;
        var mips = new RleMipLayout[mipCount];
        for (var mip = 0; mip < mipCount; mip++)
        {
            var starts = new int[profile.OffsetColumns];
            var ends = new int[profile.OffsetColumns];
            for (var column = 0; column < profile.OffsetColumns; column++)
            {
                starts[column] = checked((int)offsets[mip, column]);
                ends[column] = mip + 1 < mipCount
                    ? checked((int)offsets[mip + 1, column])
                    : streamEnds[column];
                if (starts[column] > ends[column] || starts[column] < streamStarts[column] || ends[column] > streamEnds[column])
                    throw Invalid($"mip {mip} stream {column} range is invalid.");
            }

            var expectedBlocks = checked(((long)mipWidth + 3) / 4 * (((long)mipHeight + 3) / 4));
            ValidateMip(profile, payload, mip, expectedBlocks, starts, ends);
            mips[mip] = new RleMipLayout(mipWidth, mipHeight, starts, ends);
            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
        }

        return new RleTextureLayout(mips);
    }

    private static void DecodeRle2Mip(ReadOnlySpan<byte> payload, RleMipLayout mip, Rgba32[] pixels)
    {
        var blocksWide = checked((mip.Width + 3) / 4);
        var commandCursor = mip.Starts[0];
        var commandEnd = mip.Ends[0];
        var streamCursors = (int[])mip.Starts.Clone();
        Span<byte> block = stackalloc byte[16];
        var blockIndex = 0;

        while (commandCursor < commandEnd)
        {
            var command = BinaryPrimitives.ReadUInt16LittleEndian(payload[commandCursor..]);
            commandCursor += sizeof(ushort);
            var opcode = command & 3;
            var count = command >> 2;

            for (var runIndex = 0; runIndex < count; runIndex++, blockIndex++)
            {
                if (opcode == 0)
                {
                    FillBlock(pixels, mip.Width, mip.Height, blocksWide, blockIndex,
                        new Rgba32(255, 255, 255, 0));
                    continue;
                }

                if (opcode == 1)
                {
                    payload.Slice(streamCursors[3], 2).CopyTo(block[..2]);
                    payload.Slice(streamCursors[4], 6).CopyTo(block.Slice(2, 6));
                    streamCursors[3] += 2;
                    streamCursors[4] += 6;
                }
                else
                {
                    block[0] = 0;
                    block[1] = 5;
                    block[2..8].Fill(byte.MaxValue);
                }

                payload.Slice(streamCursors[1], 4).CopyTo(block.Slice(8, 4));
                payload.Slice(streamCursors[2], 4).CopyTo(block.Slice(12, 4));
                streamCursors[1] += 4;
                streamCursors[2] += 4;
                DecodeBc3Block(block, pixels, mip.Width, mip.Height, blocksWide, blockIndex);
            }
        }

        for (var stream = 1; stream < mip.Starts.Length; stream++)
        {
            if (streamCursors[stream] != mip.Ends[stream])
                throw Invalid($"decoded mip stream {stream} ended at {streamCursors[stream]}; expected {mip.Ends[stream]}.");
        }
    }

    private static void DecodeRlesMip(
        ReadOnlySpan<byte> payload,
        RleMipLayout mip,
        Rgba32[] colorPixels,
        Rgba32[] maskPixels)
    {
        var blocksWide = checked((mip.Width + 3) / 4);
        var blocksHigh = checked((mip.Height + 3) / 4);
        var expectedBlocks = checked(blocksWide * blocksHigh);
        var commandCursor = mip.Starts[0];
        var commandEnd = mip.Ends[0];
        var streamCursors = (int[])mip.Starts.Clone();
        Span<byte> block = stackalloc byte[16];
        Span<byte> maskBlock = stackalloc byte[16];
        var blockIndex = 0;

        while (commandCursor < commandEnd)
        {
            if (commandEnd - commandCursor < sizeof(ushort))
                throw Invalid($"decoded mip command stream has a truncated 16-bit word at {commandCursor}.");

            var command = BinaryPrimitives.ReadUInt16LittleEndian(payload[commandCursor..]);
            commandCursor = checked(commandCursor + sizeof(ushort));
            var opcode = command & 3;
            var count = command >> 2;

            for (var runIndex = 0; runIndex < count; runIndex++)
            {
                if (blockIndex >= expectedBlocks)
                    throw Invalid($"decoded mip commands exceed the expected {expectedBlocks} DXT5 blocks.");

                if (opcode == 0)
                {
                    FillBlock(colorPixels, mip.Width, mip.Height, blocksWide, blockIndex,
                        new Rgba32(0, 0, 0, 0));
                    FillBlock(maskPixels, mip.Width, mip.Height, blocksWide, blockIndex,
                        new Rgba32(0, 0, 0, 255));
                    blockIndex = checked(blockIndex + 1);
                    continue;
                }

                CopyRlesStream(payload, mip, streamCursors, 3, block[..2]);
                CopyRlesStream(payload, mip, streamCursors, 4, block.Slice(2, 6));
                CopyRlesStream(payload, mip, streamCursors, 1, block.Slice(8, 4));
                CopyRlesStream(payload, mip, streamCursors, 2, block.Slice(12, 4));
                DecodeBc3Block(block, colorPixels, mip.Width, mip.Height, blocksWide, blockIndex);

                if (opcode == 1)
                {
                    CopyRlesStream(payload, mip, streamCursors, 5, maskBlock);
                    FillMaskBlock(maskPixels, mip.Width, mip.Height, blocksWide, blockIndex, maskBlock);
                }
                else
                {
                    FillBlock(maskPixels, mip.Width, mip.Height, blocksWide, blockIndex,
                        new Rgba32(255, 255, 255, 255));
                }

                blockIndex = checked(blockIndex + 1);
            }
        }

        if (blockIndex != expectedBlocks)
            throw Invalid($"decoded mip commands cover {blockIndex} blocks; expected {expectedBlocks}.");

        for (var stream = 1; stream < mip.Starts.Length; stream++)
        {
            if (streamCursors[stream] != mip.Ends[stream])
                throw Invalid($"decoded mip stream {stream} ended at {streamCursors[stream]}; expected {mip.Ends[stream]}.");
        }
    }

    private static void CopyRlesStream(
        ReadOnlySpan<byte> payload,
        RleMipLayout mip,
        int[] streamCursors,
        int stream,
        Span<byte> destination)
    {
        var cursor = streamCursors[stream];
        var end = mip.Ends[stream];
        if (cursor < mip.Starts[stream] || cursor > end || destination.Length > end - cursor)
            throw Invalid($"decoded mip stream {stream} is truncated at {cursor}; needs {destination.Length} bytes before {end}.");

        payload.Slice(cursor, destination.Length).CopyTo(destination);
        streamCursors[stream] = checked(cursor + destination.Length);
    }

    private static void FillMaskBlock(
        Rgba32[] pixels,
        int width,
        int height,
        int blocksWide,
        int blockIndex,
        ReadOnlySpan<byte> values)
    {
        var blockX = blockIndex % blocksWide;
        var blockY = blockIndex / blocksWide;
        for (var localIndex = 0; localIndex < 16; localIndex++)
        {
            var x = blockX * 4 + localIndex % 4;
            var y = blockY * 4 + localIndex / 4;
            if (x >= width || y >= height)
                continue;

            var value = values[localIndex];
            pixels[y * width + x] = new Rgba32(value, value, value, 255);
        }
    }

    private static void DecodeBc3Block(
        ReadOnlySpan<byte> block,
        Rgba32[] pixels,
        int width,
        int height,
        int blocksWide,
        int blockIndex)
    {
        Span<Rgba32> colors = stackalloc Rgba32[4];
        var color0 = ReadRgb565(BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(8, 2)));
        var color1 = ReadRgb565(BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(10, 2)));
        colors[0] = color0;
        colors[1] = color1;
        colors[2] = Interpolate(color0, color1, 2, 1);
        colors[3] = Interpolate(color0, color1, 1, 2);

        Span<byte> alphas = stackalloc byte[8];
        alphas[0] = block[0];
        alphas[1] = block[1];
        if (alphas[0] > alphas[1])
        {
            for (var index = 1; index <= 6; index++)
                alphas[index + 1] = (byte)(((7 - index) * alphas[0] + index * alphas[1]) / 7);
        }
        else
        {
            for (var index = 1; index <= 4; index++)
                alphas[index + 1] = (byte)(((5 - index) * alphas[0] + index * alphas[1]) / 5);
            alphas[6] = 0;
            alphas[7] = 255;
        }

        ulong alphaIndices = 0;
        for (var index = 0; index < 6; index++)
            alphaIndices |= (ulong)block[2 + index] << (index * 8);
        var colorIndices = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(12, 4));
        var blockX = blockIndex % blocksWide;
        var blockY = blockIndex / blocksWide;

        for (var localIndex = 0; localIndex < 16; localIndex++)
        {
            var x = blockX * 4 + localIndex % 4;
            var y = blockY * 4 + localIndex / 4;
            if (x >= width || y >= height)
                continue;

            var colorIndex = (int)((colorIndices >> (localIndex * 2)) & 3);
            var alphaIndex = (int)((alphaIndices >> (localIndex * 3)) & 7);
            var color = colors[colorIndex];
            pixels[y * width + x] = new Rgba32(color.R, color.G, color.B, alphas[alphaIndex]);
        }
    }

    private static Rgba32 ReadRgb565(ushort value)
    {
        var red = (value >> 11) & 0x1F;
        var green = (value >> 5) & 0x3F;
        var blue = value & 0x1F;
        return new Rgba32(
            Expand5(red),
            Expand6(green),
            Expand5(blue),
            255);
    }

    private static byte Expand5(int value)
    {
        var scaled = value * 255 + 16;
        return (byte)((scaled / 32 + scaled) / 32);
    }

    private static byte Expand6(int value)
    {
        var scaled = value * 255 + 32;
        return (byte)((scaled / 64 + scaled) / 64);
    }

    private static Rgba32 Interpolate(Rgba32 first, Rgba32 second, int firstWeight, int secondWeight) =>
        new(
            (byte)((firstWeight * first.R + secondWeight * second.R) / 3),
            (byte)((firstWeight * first.G + secondWeight * second.G) / 3),
            (byte)((firstWeight * first.B + secondWeight * second.B) / 3),
            255);

    private static void FillBlock(
        Rgba32[] pixels,
        int width,
        int height,
        int blocksWide,
        int blockIndex,
        Rgba32 color)
    {
        var blockX = blockIndex % blocksWide;
        var blockY = blockIndex / blocksWide;
        for (var localY = 0; localY < 4; localY++)
        {
            var y = blockY * 4 + localY;
            if (y >= height)
                continue;
            for (var localX = 0; localX < 4; localX++)
            {
                var x = blockX * 4 + localX;
                if (x < width)
                    pixels[y * width + x] = color;
            }
        }
    }

    private static void ValidateMip(
        FormatProfile profile,
        ReadOnlySpan<byte> payload,
        int mip,
        long expectedBlocks,
        int[] starts,
        int[] ends)
    {
        var commandCursor = starts[0];
        long totalBlocks = 0;
        long op1Blocks = 0;
        long op2Blocks = 0;
        while (commandCursor < ends[0])
        {
            if (ends[0] - commandCursor < sizeof(ushort))
                throw Invalid($"mip {mip} command stream has a truncated 16-bit word.");
            var command = BinaryPrimitives.ReadUInt16LittleEndian(payload[commandCursor..]);
            commandCursor += sizeof(ushort);
            var opcode = command & 3;
            var count = command >> 2;
            if (opcode == 3)
                throw Invalid($"mip {mip} contains unsupported opcode 3.");
            if (count == 0)
                throw Invalid($"mip {mip} contains a zero-count command.");
            totalBlocks = checked(totalBlocks + count);
            if (totalBlocks > expectedBlocks)
                throw Invalid($"mip {mip} command runs exceed its {expectedBlocks} DXT5 blocks.");
            if (opcode == 1)
                op1Blocks = checked(op1Blocks + count);
            else if (opcode == 2)
                op2Blocks = checked(op2Blocks + count);
        }
        if (totalBlocks != expectedBlocks)
            throw Invalid($"mip {mip} commands cover {totalBlocks} blocks; expected {expectedBlocks}.");

        var texturedBlocks = checked(op1Blocks + op2Blocks);
        var expectedBytes = new long[profile.OffsetColumns];
        expectedBytes[0] = checked((long)ends[0] - starts[0]);
        expectedBytes[1] = checked(texturedBlocks * 4);
        expectedBytes[2] = checked(texturedBlocks * 4);
        expectedBytes[3] = checked((profile.OffsetColumns == 6 ? texturedBlocks : op1Blocks) * 2);
        expectedBytes[4] = checked((profile.OffsetColumns == 6 ? texturedBlocks : op1Blocks) * 6);
        if (profile.OffsetColumns == 6)
            expectedBytes[5] = checked(op1Blocks * 16);

        for (var stream = 1; stream < profile.OffsetColumns; stream++)
        {
            var actual = (long)ends[stream] - starts[stream];
            if (actual != expectedBytes[stream])
                throw Invalid($"mip {mip} stream {stream} has {actual} bytes; commands require {expectedBytes[stream]}.");
        }
    }

    private static int GetFullMipCount(int width, int height)
    {
        var count = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            count++;
        }
        return count;
    }

    private static InvalidDataException Invalid(string message) => new(message);

    private sealed record RleTextureLayout(RleMipLayout[] Mips);

    private sealed record RleMipLayout(int Width, int Height, int[] Starts, int[] Ends);

    private readonly record struct FormatProfile(string Name, int OffsetColumns);
}
