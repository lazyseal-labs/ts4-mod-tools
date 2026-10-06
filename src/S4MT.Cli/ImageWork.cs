using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using LlamaLogic.Packages;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace S4MT.Cli;

internal sealed class ImageWork
{
    private const int MaxPngBytes = 128 * 1024 * 1024;
    private const int MaxRlesManifestBytes = 64 * 1024;
    private const long MaxPixelCount = 16_777_216;
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly JsonSerializerOptions RlesBundleJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private const string RlesColorFileName = "color.png";
    private const string RlesMaskFileName = "mask.png";
    private const string RlesManifestFileName = "manifest.json";

    internal static bool IsPngImageResource(ResourceId resourceId) =>
        resourceId.Type == unchecked((uint)ResourceType.PNGImage);

    internal static bool SupportsProjectValidation(ResourceId resourceId) =>
        IsPngImageResource(resourceId) ||
        resourceId.Type == LrleCodec.ResourceTypeId ||
        RleTextureCodec.Supports(resourceId) ||
        resourceId.Type == unchecked((uint)ResourceType.DSTImage) ||
        IsTranslucentJpeg(resourceId);

    internal static string ProjectValidationDiagnosticCode(ResourceId resourceId) => resourceId.Type switch
    {
        var type when type == unchecked((uint)ResourceType.PNGImage) => "invalid_png_image",
        var type when type == LrleCodec.ResourceTypeId => "invalid_lrle_image",
        var type when type == RleTextureCodec.Rle2ResourceTypeId => "invalid_rle2_image",
        var type when type == RleTextureCodec.RlesResourceTypeId => "invalid_rles_image",
        var type when type == unchecked((uint)ResourceType.DSTImage) => "invalid_dst_image",
        _ when IsTranslucentJpeg(resourceId) => "invalid_tjpeg_image",
        _ => throw new CliInputException($"Image resource {resourceId} has no project validation codec.")
    };

    internal static void ValidatePngImagePayload(ResourceId resourceId, ReadOnlyMemory<byte> payload)
    {
        if (!IsPngImageResource(resourceId))
            throw new CliInputException($"Resource {resourceId} is not a PNGImage resource.");

        try
        {
            _ = ValidatePng(payload.Span);
        }
        catch (CliInputException exception)
        {
            throw new CliInputException($"Invalid PNGImage resource {resourceId}: {exception.Message}");
        }
        catch (ImageFormatException exception)
        {
            throw new CliInputException($"Invalid PNGImage resource {resourceId}: {exception.Message}");
        }
        catch (InvalidDataException exception)
        {
            throw new CliInputException($"Invalid PNGImage resource {resourceId}: {exception.Message}");
        }
    }

    internal async Task ValidateProjectPayloadAsync(
        ResourceId resourceId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (IsPngImageResource(resourceId))
        {
            ValidatePngImagePayload(resourceId, payload);
            return;
        }

        if (!SupportsProjectValidation(resourceId))
            throw new CliInputException($"Image resource {resourceId} has no project validation codec.");

        if (RleTextureCodec.Supports(resourceId))
        {
            try
            {
                RleTextureCodec.Validate(resourceId, payload.Span);
            }
            catch (InvalidDataException exception)
            {
                throw new CliInputException(exception.Message);
            }
            return;
        }

        var codec = GetExportCodec(resourceId);
        var key = ToPackageKey(resourceId);
        try
        {
            if (codec == "lrle")
            {
                LrleCodec.Validate(payload.Span);
                return;
            }
            await using var package = new DataBasePackedFile();
            await package.SetAsync(key, payload, CompressionMode.Auto, cancellationToken);
            var png = await GetPngAsync(package, key, codec, cancellationToken);
            _ = ValidatePng(png.Span);
        }
        catch (CliInputException exception)
        {
            throw new CliInputException($"Invalid {codec} image resource {resourceId}: {exception.Message}");
        }
        catch (Exception exception) when (IsExpectedImageValidationFailure(exception))
        {
            throw new CliInputException(
                $"Invalid {codec} image resource {resourceId} ({exception.GetType().Name}): {exception.Message}");
        }
    }

    public async Task<ImageExportReport> ExportAsync(
        string packagePath,
        ResourceId resourceId,
        string pngPath,
        CancellationToken cancellationToken,
        int? mipLevel = null)
    {
        var codec = GetExportCodec(resourceId);
        if (mipLevel is not null && codec != "rle2")
            throw new CliInputException("The --mip option is available only for RLE2 image export.");

        var fullPackagePath = Path.GetFullPath(packagePath);
        var fullPngPath = Path.GetFullPath(pngPath);
        PathGuard.EnsureDoesNotOverwrite(fullPngPath, (fullPackagePath, "source package"));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPngPath)!);
        var temporaryPath = CreateTemporaryPath(fullPngPath);

        try
        {
            ReadOnlyMemory<byte> png;
            int? exportedMipLevel = null;
            int? exportedMipCount = null;
            await using (var package = await DataBasePackedFile.FromPathAsync(fullPackagePath))
            {
                var key = ToPackageKey(resourceId);
                if (!await package.ContainsKeyAsync(key, cancellationToken))
                    throw new CliInputException($"Package does not contain image resource {resourceId}.");

                if (codec == "rle2")
                {
                    var payload = await package.GetAsync(key, force: false, cancellationToken);
                    RleTextureCodec.Rle2MipExport decoded;
                    try
                    {
                        decoded = RleTextureCodec.DecodeRle2MipToPng(resourceId, payload.Span, mipLevel ?? 0);
                    }
                    catch (InvalidDataException exception)
                    {
                        throw new CliInputException(exception.Message);
                    }
                    png = decoded.PngBytes;
                    exportedMipLevel = decoded.MipLevel;
                    exportedMipCount = decoded.MipCount;
                }
                else
                    png = await GetPngAsync(package, key, codec, cancellationToken);
            }

            var imageInfo = ValidatePng(png.Span);
            await File.WriteAllBytesAsync(temporaryPath, png.ToArray(), cancellationToken);
            File.Move(temporaryPath, fullPngPath, overwrite: true);
            return new ImageExportReport(
                fullPackagePath,
                fullPngPath,
                resourceId.ToString(),
                codec,
                imageInfo.Width,
                imageInfo.Height,
                png.Length,
                exportedMipLevel,
                exportedMipCount);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<RlesExportReport> ExportRlesAsync(
        string packagePath,
        ResourceId resourceId,
        string bundlePath,
        CancellationToken cancellationToken,
        int? mipLevel = null)
    {
        EnsureRlesResource(resourceId);
        var fullPackagePath = Path.GetFullPath(packagePath);
        var fullBundlePath = Path.GetFullPath(bundlePath);
        PathGuard.EnsureDoesNotOverwrite(fullBundlePath, (fullPackagePath, "source package"));
        PathGuard.EnsureOutsideDirectory(fullPackagePath, fullBundlePath, "RLES bundle");
        if (Directory.Exists(fullBundlePath) || File.Exists(fullBundlePath))
            throw new CliInputException($"RLES bundle destination already exists: {fullBundlePath}");

        var parentDirectory = Path.GetDirectoryName(fullBundlePath)!;
        Directory.CreateDirectory(parentDirectory);
        var temporaryDirectory = Path.Combine(
            parentDirectory,
            $".{Path.GetFileName(fullBundlePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            RleTextureCodec.RlesMipPngExport decoded;
            await using (var package = await DataBasePackedFile.FromPathAsync(fullPackagePath))
            {
                var key = ToPackageKey(resourceId);
                if (!await package.ContainsKeyAsync(key, cancellationToken))
                    throw new CliInputException($"Package does not contain RLES image resource {resourceId}.");

                var payload = await package.GetAsync(key, force: false, cancellationToken);
                try
                {
                    decoded = RleTextureCodec.DecodeRlesMipToPngs(resourceId, payload.Span, mipLevel ?? 0);
                }
                catch (InvalidDataException exception)
                {
                    throw new CliInputException(exception.Message);
                }
            }

            var colorInfo = ValidatePng(decoded.ColorPngBytes);
            var maskInfo = ValidatePng(decoded.MaskPngBytes);
            if (colorInfo != (decoded.Width, decoded.Height) || maskInfo != colorInfo)
                throw new InvalidDataException("RLES decoder returned mismatched plane dimensions.");
            ValidateRlesMask(decoded.MaskPngBytes, decoded.Width, decoded.Height);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(temporaryDirectory);
            var colorPngPath = Path.Combine(temporaryDirectory, RlesColorFileName);
            var maskPngPath = Path.Combine(temporaryDirectory, RlesMaskFileName);
            var manifestPath = Path.Combine(temporaryDirectory, RlesManifestFileName);
            await File.WriteAllBytesAsync(colorPngPath, decoded.ColorPngBytes, cancellationToken);
            await File.WriteAllBytesAsync(maskPngPath, decoded.MaskPngBytes, cancellationToken);

            var manifest = new RlesBundleManifest(
                SchemaVersion: 1,
                Codec: "RLES",
                SourceTgi: FormatBundleTgi(resourceId),
                SourceWidth: decoded.BaseWidth,
                SourceHeight: decoded.BaseHeight,
                Width: decoded.Width,
                Height: decoded.Height,
                MipLevel: decoded.MipLevel,
                MipCount: decoded.MipCount,
                ColorFile: RlesColorFileName,
                ColorFormat: "rgba8",
                MaskFile: RlesMaskFileName,
                MaskFormat: "grayscale8-opaque");
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, RlesBundleJsonOptions);
            await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporaryDirectory, fullBundlePath);

            return new RlesExportReport(
                fullPackagePath,
                fullBundlePath,
                Path.Combine(fullBundlePath, RlesColorFileName),
                Path.Combine(fullBundlePath, RlesMaskFileName),
                resourceId.ToString(),
                decoded.BaseWidth,
                decoded.BaseHeight,
                decoded.Width,
                decoded.Height,
                decoded.MipLevel,
                decoded.MipCount,
                decoded.ColorPngBytes.Length,
                decoded.MaskPngBytes.Length);
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    public async Task<ImageBuildReport> BuildAsync(
        ResourceId resourceId,
        string pngPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var codec = GetBuildCodec(resourceId);
        var fullPngPath = Path.GetFullPath(pngPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        PathGuard.EnsureDoesNotOverwrite(fullOutputPath, (fullPngPath, "PNG input"));
        var png = await ReadPngAsync(fullPngPath, cancellationToken);
        var originalImage = ValidatePng(png);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        var temporaryPath = CreateTemporaryPath(fullOutputPath);

        try
        {
            var key = ToPackageKey(resourceId);
            await using (var package = new DataBasePackedFile())
            {
                await SetPngAsync(package, key, codec, png, cancellationToken);
                await package.SaveAsAsync(temporaryPath);
            }

            ImageRoundtripResult verification;
            await using (var candidate = await DataBasePackedFile.FromPathAsync(temporaryPath))
            {
                var keys = await candidate.GetKeysAsync(cancellationToken: cancellationToken);
                if (keys.Count != 1 || keys[0] != key)
                    throw new InvalidDataException("Image build did not preserve its exact resource key.");
                var decodedPng = await GetPngAsync(candidate, key, codec, cancellationToken);
                if (codec == "png" && !png.AsSpan().SequenceEqual(decodedPng.Span))
                    throw new InvalidDataException("PNGImage build changed the original PNG payload bytes.");
                verification = ComparePngPixels(png, decodedPng);
                if (verification.Width != originalImage.Width || verification.Height != originalImage.Height)
                    throw new InvalidDataException("Image build changed the PNG dimensions.");
                RequireExactLrlePixels(codec, verification);
            }

            var digest = await ComputeSha256Async(temporaryPath, cancellationToken);
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            return new ImageBuildReport(
                fullOutputPath,
                resourceId.ToString(),
                originalImage.Width,
                originalImage.Height,
                verification.IdenticalPixels,
                verification.TotalPixels,
                verification.MaximumChannelDelta,
                verification.MeanChannelDelta,
                digest,
                ReadBackVerified: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<RlesBuildReport> BuildRlesAsync(
        ResourceId resourceId,
        string bundlePath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        EnsureRlesResource(resourceId);
        var bundle = await ReadRlesBundleAsync(bundlePath, cancellationToken);
        RequireBaseMip(bundle.Manifest);

        var fullOutputPath = Path.GetFullPath(outputPath);
        PathGuard.EnsureOutsideDirectory(fullOutputPath, bundle.BundlePath, "RLES bundle");
        PathGuard.EnsureDoesNotOverwrite(
            fullOutputPath,
            (bundle.ColorPngPath, "RLES color PNG input"),
            (bundle.MaskPngPath, "RLES mask PNG input"),
            (Path.Combine(bundle.BundlePath, RlesManifestFileName), "RLES bundle manifest"));
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        var temporaryPath = CreateTemporaryPath(fullOutputPath);

        try
        {
            var encoded = RleTextureCodec.EncodeRlesPngs(resourceId, bundle.ColorPngBytes, bundle.MaskPngBytes);
            if (encoded.Width != bundle.Manifest.Width || encoded.Height != bundle.Manifest.Height)
                throw new InvalidDataException("RLES encoder changed the input plane dimensions.");

            var key = ToPackageKey(resourceId);
            await using (var package = new DataBasePackedFile())
            {
                await package.SetAsync(key, encoded.Payload, CompressionMode.Auto, cancellationToken);
                await package.SaveAsAsync(temporaryPath);
            }

            RlesRoundtripVerification verification;
            await using (var candidate = await DataBasePackedFile.FromPathAsync(temporaryPath))
            {
                var keys = await candidate.GetKeysAsync(cancellationToken: cancellationToken);
                if (keys.Count != 1 || keys[0] != key)
                    throw new InvalidDataException("RLES build did not preserve its exact resource key.");

                verification = await VerifyRlesCandidateAsync(
                    candidate,
                    key,
                    resourceId,
                    bundle.ColorPngBytes,
                    bundle.MaskPngBytes,
                    cancellationToken);
                if (verification.MipCount != encoded.MipCount)
                    throw new InvalidDataException("RLES package read-back changed the generated mip count.");
            }

            var digest = await ComputeSha256Async(temporaryPath, cancellationToken);
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            return new RlesBuildReport(
                fullOutputPath,
                bundle.BundlePath,
                resourceId.ToString(),
                verification.Width,
                verification.Height,
                verification.MipCount,
                verification.PixelVerifiedMipCount,
                verification.AllMipsDecoded,
                verification.Color,
                verification.Mask,
                digest,
                ReadBackVerified: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<ImageImportReport> ImportAsync(
        string packagePath,
        ResourceId resourceId,
        string pngPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var codec = GetBuildCodec(resourceId);
        var fullPackagePath = Path.GetFullPath(packagePath);
        var fullPngPath = Path.GetFullPath(pngPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        PathGuard.EnsureDoesNotOverwrite(
            fullOutputPath,
            (fullPackagePath, "source package"),
            (fullPngPath, "PNG input"));

        var png = await ReadPngAsync(fullPngPath, cancellationToken);
        var originalImage = ValidatePng(png);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        var temporaryPath = CreateTemporaryPath(fullOutputPath);

        try
        {
            var key = ToPackageKey(resourceId);
            int packageResourceCount;
            ImageRoundtripResult verification;
            await using (var source = await DataBasePackedFile.FromPathAsync(fullPackagePath))
            {
                if (!await source.ContainsKeyAsync(key, cancellationToken))
                    throw new CliInputException($"Source package does not contain image resource {resourceId}.");
                var originalKeys = (await source.GetKeysAsync(cancellationToken: cancellationToken)).ToHashSet();
                packageResourceCount = originalKeys.Count;
                await SetPngAsync(source, key, codec, png, cancellationToken);
                await source.SaveAsAsync(temporaryPath);

                await using var candidate = await DataBasePackedFile.FromPathAsync(temporaryPath);
                var candidateKeys = (await candidate.GetKeysAsync(cancellationToken: cancellationToken)).ToHashSet();
                if (!originalKeys.SetEquals(candidateKeys))
                    throw new InvalidDataException("Image import changed the package resource-key set.");

                foreach (var unchangedKey in originalKeys.Where(existingKey => existingKey != key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var before = await source.GetAsync(unchangedKey, force: false, cancellationToken);
                    var after = await candidate.GetAsync(unchangedKey, force: false, cancellationToken);
                    if (!before.Span.SequenceEqual(after.Span))
                        throw new InvalidDataException($"Image import changed unrelated resource {ToResourceId(unchangedKey)}.");
                }

                var decodedPng = await GetPngAsync(candidate, key, codec, cancellationToken);
                if (codec == "png" && !png.AsSpan().SequenceEqual(decodedPng.Span))
                    throw new InvalidDataException("PNGImage import changed the original PNG payload bytes.");
                verification = ComparePngPixels(png, decodedPng);
                if (verification.Width != originalImage.Width || verification.Height != originalImage.Height)
                    throw new InvalidDataException("Image import changed the PNG dimensions.");
                RequireExactLrlePixels(codec, verification);
            }

            var digest = await ComputeSha256Async(temporaryPath, cancellationToken);
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            return new ImageImportReport(
                fullPackagePath,
                fullOutputPath,
                resourceId.ToString(),
                originalImage.Width,
                originalImage.Height,
                packageResourceCount,
                verification.IdenticalPixels,
                verification.TotalPixels,
                verification.MaximumChannelDelta,
                verification.MeanChannelDelta,
                digest,
                ReadBackVerified: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<RlesImportReport> ImportRlesAsync(
        string packagePath,
        ResourceId resourceId,
        string bundlePath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        EnsureRlesResource(resourceId);
        var fullPackagePath = Path.GetFullPath(packagePath);
        var bundle = await ReadRlesBundleAsync(bundlePath, cancellationToken);
        RequireBaseMip(bundle.Manifest);

        var fullOutputPath = Path.GetFullPath(outputPath);
        PathGuard.EnsureOutsideDirectory(fullOutputPath, bundle.BundlePath, "RLES bundle");
        PathGuard.EnsureDoesNotOverwrite(
            fullOutputPath,
            (fullPackagePath, "source package"),
            (bundle.ColorPngPath, "RLES color PNG input"),
            (bundle.MaskPngPath, "RLES mask PNG input"),
            (Path.Combine(bundle.BundlePath, RlesManifestFileName), "RLES bundle manifest"));
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        var temporaryPath = CreateTemporaryPath(fullOutputPath);

        try
        {
            var encoded = RleTextureCodec.EncodeRlesPngs(resourceId, bundle.ColorPngBytes, bundle.MaskPngBytes);
            if (encoded.Width != bundle.Manifest.Width || encoded.Height != bundle.Manifest.Height)
                throw new InvalidDataException("RLES encoder changed the input plane dimensions.");

            var key = ToPackageKey(resourceId);
            int packageResourceCount;
            RlesRoundtripVerification verification;
            await using (var source = await DataBasePackedFile.FromPathAsync(fullPackagePath))
            {
                if (!await source.ContainsKeyAsync(key, cancellationToken))
                    throw new CliInputException($"Source package does not contain RLES image resource {resourceId}.");
                var originalKeys = (await source.GetKeysAsync(cancellationToken: cancellationToken)).ToHashSet();
                packageResourceCount = originalKeys.Count;
                await source.SetAsync(key, encoded.Payload, CompressionMode.Auto, cancellationToken);
                await source.SaveAsAsync(temporaryPath);

                await using var candidate = await DataBasePackedFile.FromPathAsync(temporaryPath);
                var candidateKeys = (await candidate.GetKeysAsync(cancellationToken: cancellationToken)).ToHashSet();
                if (!originalKeys.SetEquals(candidateKeys))
                    throw new InvalidDataException("RLES import changed the package resource-key set.");

                foreach (var unchangedKey in originalKeys.Where(existingKey => existingKey != key))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var before = await source.GetAsync(unchangedKey, force: false, cancellationToken);
                    var after = await candidate.GetAsync(unchangedKey, force: false, cancellationToken);
                    if (!before.Span.SequenceEqual(after.Span))
                        throw new InvalidDataException(
                            $"RLES import changed unrelated resource {ToResourceId(unchangedKey)}.");
                }

                verification = await VerifyRlesCandidateAsync(
                    candidate,
                    key,
                    resourceId,
                    bundle.ColorPngBytes,
                    bundle.MaskPngBytes,
                    cancellationToken);
                if (verification.MipCount != encoded.MipCount)
                    throw new InvalidDataException("RLES package read-back changed the generated mip count.");
            }

            var digest = await ComputeSha256Async(temporaryPath, cancellationToken);
            File.Move(temporaryPath, fullOutputPath, overwrite: true);
            return new RlesImportReport(
                fullPackagePath,
                fullOutputPath,
                bundle.BundlePath,
                resourceId.ToString(),
                verification.Width,
                verification.Height,
                packageResourceCount,
                verification.MipCount,
                verification.PixelVerifiedMipCount,
                verification.AllMipsDecoded,
                verification.Color,
                verification.Mask,
                digest,
                ReadBackVerified: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static async Task<RlesRoundtripVerification> VerifyRlesCandidateAsync(
        DataBasePackedFile candidate,
        ResourceKey key,
        ResourceId resourceId,
        byte[] colorPng,
        byte[] maskPng,
        CancellationToken cancellationToken)
    {
        var payload = await candidate.GetAsync(key, force: false, cancellationToken);
        RleTextureCodec.Validate(resourceId, payload.Span);
        using var expectedColor = Image.Load<Rgba32>(colorPng);
        using var expectedMask = Image.Load<Rgba32>(maskPng);
        if (expectedColor.Width != expectedMask.Width || expectedColor.Height != expectedMask.Height)
            throw new InvalidDataException("RLES input plane dimensions differ during package read-back.");
        var baseWidth = expectedColor.Width;
        var baseHeight = expectedColor.Height;
        ValidateRlesMask(maskPng, expectedMask.Width, expectedMask.Height);

        var colorComparison = new PlaneComparisonAccumulator();
        var maskComparison = new PlaneComparisonAccumulator();
        int? mipCount = null;
        var width = expectedColor.Width;
        var height = expectedColor.Height;

        for (var mipLevel = 0; ; mipLevel++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decoded = RleTextureCodec.DecodeRlesMipToPngs(resourceId, payload.Span, mipLevel);
            mipCount ??= decoded.MipCount;
            if (decoded.MipCount != mipCount || decoded.MipLevel != mipLevel ||
                decoded.BaseWidth != baseWidth || decoded.BaseHeight != baseHeight ||
                decoded.Width != width || decoded.Height != height)
                throw new InvalidDataException($"RLES mip {mipLevel} dimensions or metadata changed during read-back.");

            ValidateRlesMask(decoded.MaskPngBytes, width, height);
            var expectedColorPng = EncodePng(expectedColor);
            var expectedMaskPng = EncodePng(expectedMask);
            var colorPixels = ComparePngPixels(expectedColorPng, decoded.ColorPngBytes);
            var maskPixels = ComparePngPixels(expectedMaskPng, decoded.MaskPngBytes);
            if (maskPixels.IdenticalPixels != maskPixels.TotalPixels || maskPixels.MaximumChannelDelta != 0)
                throw new InvalidDataException($"RLES mask mip {mipLevel} changed during package read-back.");
            colorComparison.Add(colorPixels);
            maskComparison.Add(maskPixels);

            if (mipLevel + 1 == mipCount)
                break;

            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            expectedColor.Mutate(context => context.Resize(width, height, KnownResamplers.Box));
            expectedMask.Mutate(context => context.Resize(width, height, KnownResamplers.Box));
        }

        return new RlesRoundtripVerification(
            baseWidth,
            baseHeight,
            mipCount!.Value,
            mipCount.Value,
            AllMipsDecoded: true,
            colorComparison.ToReport(),
            maskComparison.ToReport());
    }

    private static byte[] EncodePng(Image<Rgba32> image)
    {
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        if (stream.Length > MaxPngBytes)
            throw new InvalidDataException("RLES read-back PNG exceeds the 128 MiB limit.");
        return stream.ToArray();
    }

    private static void RequireBaseMip(RlesBundleManifest manifest)
    {
        if (manifest.MipLevel != 0 || manifest.Width != manifest.SourceWidth ||
            manifest.Height != manifest.SourceHeight)
            throw new CliInputException(
                $"RLES build/import requires bundle mip 0; this bundle contains mip {manifest.MipLevel}.");
    }

    private static async Task<ReadOnlyMemory<byte>> GetPngAsync(
        DataBasePackedFile package,
        ResourceKey key,
        string codec,
        CancellationToken cancellationToken)
    {
        if (codec == "rle2")
        {
            var payload = await package.GetAsync(key, force: false, cancellationToken);
            return RleTextureCodec.DecodeRle2MipToPng(ToResourceId(key), payload.Span, 0).PngBytes;
        }

        return codec switch
        {
            "dds" => await package.GetDdsAsPngAsync(key, force: false, cancellationToken),
            "png" => await package.GetAsync(key, force: false, cancellationToken),
            "tjpeg" => await package.GetTranslucentJpegAsPngAsync(key, force: false, cancellationToken),
            "lrle" => LrleCodec.DecodeToPng((await package.GetAsync(key, force: false, cancellationToken)).Span),
            _ => throw new InvalidOperationException($"Unknown image codec '{codec}'.")
        };
    }

    private static Task SetPngAsync(
        DataBasePackedFile package,
        ResourceKey key,
        string codec,
        ReadOnlyMemory<byte> png,
        CancellationToken cancellationToken) => codec switch
    {
        "dds" => package.SetPngAsDdsAsync(key, png, cancellationToken),
        "png" => package.SetAsync(key, png, CompressionMode.Auto, cancellationToken),
        "tjpeg" => package.SetPngAsTranslucentJpegAsync(key, png, cancellationToken),
        "lrle" => package.SetAsync(key, LrleCodec.EncodePng(png.Span), CompressionMode.Auto, cancellationToken),
        "rle2" => package.SetAsync(
            key,
            RleTextureCodec.EncodeRle2Png(ToResourceId(key), png.Span),
            CompressionMode.Auto,
            cancellationToken),
        _ => throw new InvalidOperationException($"Unknown image codec '{codec}'.")
    };

    private static string GetExportCodec(ResourceId id) => id.Type switch
    {
        var type when type == unchecked((uint)ResourceType.DSTImage) => "dds",
        var type when type == unchecked((uint)ResourceType.PNGImage) => "png",
        var type when type == LrleCodec.ResourceTypeId => "lrle",
        var type when type == RleTextureCodec.Rle2ResourceTypeId => "rle2",
        _ when IsTranslucentJpeg(id) => "tjpeg",
        _ => throw new CliInputException(
            $"Image type 0x{id.Type:X8} has no verified PNG export codec. Supported types are DSTImage (0x00B2D882), PNGImage (0x2F7D0004), LRLEImage (0x2BC04EDF), RLE2Image (0x3453CF95), and types tagged as TS4 translucent JPEG by LlamaLogic; RLES mask export and PNGImage2 remain unsupported.")
    };

    private static string GetBuildCodec(ResourceId id) => id.Type switch
    {
        var type when type == unchecked((uint)ResourceType.DSTImage) => "dds",
        var type when type == unchecked((uint)ResourceType.PNGImage) => "png",
        var type when type == LrleCodec.ResourceTypeId => "lrle",
        var type when type == RleTextureCodec.Rle2ResourceTypeId => "rle2",
        _ when IsTranslucentJpeg(id) => "tjpeg",
        _ => throw new CliInputException(
            $"Image type 0x{id.Type:X8} has no verified PNG import codec. Writable types are DSTImage (0x00B2D882), PNGImage (0x2F7D0004), LRLEImage (0x2BC04EDF), RLE2Image (0x3453CF95), and types tagged as TS4 translucent JPEG by LlamaLogic.")
    };

    private static void RequireExactLrlePixels(string codec, ImageRoundtripResult verification)
    {
        if (codec == "lrle" &&
            (verification.IdenticalPixels != verification.TotalPixels || verification.MaximumChannelDelta != 0))
            throw new InvalidDataException("LRLE read-back changed RGBA pixels.");
    }

    private static bool IsTranslucentJpeg(ResourceId id)
    {
        var name = Enum.GetName(typeof(ResourceType), unchecked((ResourceType)id.Type));
        var member = name is null ? null : typeof(ResourceType).GetField(name);
        var attribute = member?.GetCustomAttribute<ResourceFileTypeAttribute>();
        return attribute?.ResourceFileType == ResourceFileType.Ts4TranslucentJointPhotographicExpertsGroupImage;
    }

    private static bool IsExpectedImageValidationFailure(Exception exception) => exception is
        ArgumentException or
        EndOfStreamException or
        FormatException or
        ImageFormatException or
        IndexOutOfRangeException or
        InvalidDataException or
        InvalidOperationException or
        NotSupportedException or
        OverflowException;

    private static async Task<byte[]> ReadPngAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new CliInputException($"PNG file does not exist: {path}");
        if (info.Length > MaxPngBytes)
            throw new CliInputException("PNG file exceeds the 128 MiB input limit.");
        var png = await File.ReadAllBytesAsync(path, cancellationToken);
        _ = ValidatePng(png);
        return png;
    }

    private static async Task<RlesBundleInput> ReadRlesBundleAsync(
        string bundlePath,
        CancellationToken cancellationToken)
    {
        var fullBundlePath = Path.GetFullPath(bundlePath);
        if (!Directory.Exists(fullBundlePath))
            throw new CliInputException($"RLES bundle directory does not exist: {fullBundlePath}");

        var manifestPath = Path.Combine(fullBundlePath, RlesManifestFileName);
        var colorPngPath = Path.Combine(fullBundlePath, RlesColorFileName);
        var maskPngPath = Path.Combine(fullBundlePath, RlesMaskFileName);
        PathGuard.EnsureWithinDirectory(manifestPath, fullBundlePath, "RLES bundle");
        PathGuard.EnsureWithinDirectory(colorPngPath, fullBundlePath, "RLES bundle");
        PathGuard.EnsureWithinDirectory(maskPngPath, fullBundlePath, "RLES bundle");

        var manifestInfo = new FileInfo(manifestPath);
        if (!manifestInfo.Exists)
            throw new CliInputException($"RLES bundle manifest does not exist: {manifestPath}");
        if (manifestInfo.Length > MaxRlesManifestBytes)
            throw new CliInputException("RLES bundle manifest exceeds the 64 KiB input limit.");

        var manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
        RlesBundleManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<RlesBundleManifest>(manifestBytes, RlesBundleJsonOptions)
                ?? throw new JsonException("Manifest root must be an object.");
        }
        catch (JsonException exception)
        {
            throw new CliInputException($"RLES bundle manifest is invalid JSON: {exception.Message}");
        }

        ValidateRlesManifest(manifest);
        var colorPng = await ReadPngAsync(colorPngPath, cancellationToken);
        var maskPng = await ReadPngAsync(maskPngPath, cancellationToken);
        var colorInfo = ValidatePng(colorPng);
        var maskInfo = ValidatePng(maskPng);
        if (colorInfo != maskInfo || colorInfo != (manifest.Width, manifest.Height))
            throw new CliInputException(
                $"RLES bundle PNG dimensions must both match the manifest's {manifest.Width}x{manifest.Height} dimensions.");
        ValidateRlesMask(maskPng, manifest.Width, manifest.Height);

        return new RlesBundleInput(
            fullBundlePath,
            manifest,
            colorPngPath,
            maskPngPath,
            colorPng,
            maskPng);
    }

    private static void ValidateRlesManifest(RlesBundleManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.Codec != "RLES")
            throw new CliInputException("RLES bundle must use schemaVersion 1 and codec 'RLES'.");
        if (manifest.ColorFile != RlesColorFileName || manifest.MaskFile != RlesMaskFileName ||
            manifest.ColorFormat != "rgba8" || manifest.MaskFormat != "grayscale8-opaque")
            throw new CliInputException("RLES bundle plane names and decoded pixel formats are not supported.");
        try
        {
            _ = ResourceIdParser.ParseTgi(manifest.SourceTgi);
        }
        catch (CliInputException exception)
        {
            throw new CliInputException($"RLES bundle sourceTgi is invalid: {exception.Message}");
        }

        if (manifest.SourceWidth is < 1 or > 16384 || manifest.SourceHeight is < 1 or > 16384 ||
            (long)manifest.SourceWidth * manifest.SourceHeight > MaxPixelCount)
            throw new CliInputException("RLES bundle source dimensions are outside the supported range.");
        var maximumMipCount = GetMipCount(manifest.SourceWidth, manifest.SourceHeight);
        if (manifest.MipCount is < 1 or > 15 || manifest.MipCount > maximumMipCount ||
            manifest.MipLevel < 0 || manifest.MipLevel >= manifest.MipCount)
            throw new CliInputException("RLES bundle mip metadata is outside the supported range.");

        var expectedWidth = GetMipDimension(manifest.SourceWidth, manifest.MipLevel);
        var expectedHeight = GetMipDimension(manifest.SourceHeight, manifest.MipLevel);
        if (manifest.Width != expectedWidth || manifest.Height != expectedHeight)
            throw new CliInputException("RLES bundle selected dimensions do not match its source dimensions and mip level.");
    }

    private static void ValidateRlesMask(ReadOnlyMemory<byte> png, int expectedWidth, int expectedHeight)
    {
        using var image = Image.Load<Rgba32>(png.ToArray());
        if (image.Width != expectedWidth || image.Height != expectedHeight)
            throw new CliInputException("RLES mask dimensions do not match the color plane.");

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var pixel = image[x, y];
                if (pixel.R != pixel.G || pixel.G != pixel.B || pixel.A != byte.MaxValue)
                    throw new CliInputException(
                        $"RLES mask must have equal RGB channels and alpha 255; invalid pixel at ({x}, {y}).");
            }
        }
    }

    private static int GetMipCount(int width, int height)
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

    private static int GetMipDimension(int baseDimension, int mipLevel)
    {
        for (var level = 0; level < mipLevel; level++)
            baseDimension = Math.Max(1, baseDimension / 2);
        return baseDimension;
    }

    private static string FormatBundleTgi(ResourceId id) =>
        $"{id.Type:X8}:{id.Group:X8}:{id.Instance:X16}";

    private static void EnsureRlesResource(ResourceId resourceId)
    {
        if (resourceId.Type != RleTextureCodec.RlesResourceTypeId)
            throw new CliInputException($"Resource {resourceId} is not an RLES image resource.");
    }

    private static (int Width, int Height) ValidatePng(ReadOnlySpan<byte> png)
    {
        var (width, height) = ValidatePngHeader(png);

        using var image = Image.Load<Rgba32>(png.ToArray());
        if (image.Width != width || image.Height != height)
            throw new InvalidDataException("PNG header dimensions did not match its decoded image.");
        return (width, height);
    }

    private static ImageRoundtripResult ComparePngPixels(
        ReadOnlyMemory<byte> originalPng,
        ReadOnlyMemory<byte> decodedPng)
    {
        var (originalWidth, originalHeight) = ValidatePngHeader(originalPng.Span);
        var (decodedWidth, decodedHeight) = ValidatePngHeader(decodedPng.Span);
        if (originalWidth != decodedWidth || originalHeight != decodedHeight)
            throw new InvalidDataException("Image codec changed the PNG dimensions.");

        using var original = Image.Load<Rgba32>(originalPng.ToArray());
        using var decoded = Image.Load<Rgba32>(decodedPng.ToArray());
        if (original.Width != decoded.Width || original.Height != decoded.Height)
            throw new InvalidDataException("Image codec changed the PNG dimensions.");

        var totalPixels = checked(original.Width * original.Height);
        var originalPixels = new Rgba32[totalPixels];
        var decodedPixels = new Rgba32[totalPixels];
        original.CopyPixelDataTo(originalPixels);
        decoded.CopyPixelDataTo(decodedPixels);

        long identicalPixels = 0;
        long totalChannelDelta = 0;
        var maximumChannelDelta = 0;
        for (var pixelIndex = 0; pixelIndex < totalPixels; pixelIndex++)
        {
            var before = originalPixels[pixelIndex];
            var after = decodedPixels[pixelIndex];
            if (before == after)
                identicalPixels++;

            maximumChannelDelta = Math.Max(maximumChannelDelta, ChannelDelta(before.R, after.R));
            maximumChannelDelta = Math.Max(maximumChannelDelta, ChannelDelta(before.G, after.G));
            maximumChannelDelta = Math.Max(maximumChannelDelta, ChannelDelta(before.B, after.B));
            maximumChannelDelta = Math.Max(maximumChannelDelta, ChannelDelta(before.A, after.A));
            totalChannelDelta += ChannelDelta(before.R, after.R);
            totalChannelDelta += ChannelDelta(before.G, after.G);
            totalChannelDelta += ChannelDelta(before.B, after.B);
            totalChannelDelta += ChannelDelta(before.A, after.A);
        }

        return new ImageRoundtripResult(
            original.Width,
            original.Height,
            identicalPixels,
            totalPixels,
            maximumChannelDelta,
            totalChannelDelta / (double)(totalPixels * 4));
    }

    private static int ChannelDelta(byte left, byte right) => Math.Abs(left - right);

    private static (int Width, int Height) ValidatePngHeader(ReadOnlySpan<byte> png)
    {
        if (png.Length > MaxPngBytes)
            throw new CliInputException("PNG data exceeds the 128 MiB limit.");
        if (png.Length < 24 || !png[..8].SequenceEqual(PngSignature) ||
            !png.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new CliInputException("Image codec output is not a valid PNG with an IHDR chunk.");

        var width = BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4));
        var height = BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4));
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || (long)width * height > MaxPixelCount)
            throw new CliInputException("PNG dimensions must be 1–16384 per side and at most 16,777,216 pixels total.");
        return (width, height);
    }

    private static ResourceKey ToPackageKey(ResourceId id) =>
        new(unchecked((ResourceType)id.Type), id.Group, id.Instance);

    private static ResourceId ToResourceId(ResourceKey key) =>
        new(unchecked((uint)key.Type), key.Group, key.FullInstance);

    private static string CreateTemporaryPath(string outputPath) =>
        Path.Combine(Path.GetDirectoryName(outputPath)!, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private sealed record ImageRoundtripResult(
        int Width,
        int Height,
        long IdenticalPixels,
        long TotalPixels,
        int MaximumChannelDelta,
        double MeanChannelDelta);

    private sealed record RlesRoundtripVerification(
        int Width,
        int Height,
        int MipCount,
        int PixelVerifiedMipCount,
        bool AllMipsDecoded,
        ImagePlaneComparison Color,
        ImagePlaneComparison Mask);

    private sealed class PlaneComparisonAccumulator
    {
        private long _identicalPixels;
        private long _totalPixels;
        private double _totalChannelDelta;
        private int _maximumChannelDelta;

        public void Add(ImageRoundtripResult result)
        {
            _identicalPixels = checked(_identicalPixels + result.IdenticalPixels);
            _totalPixels = checked(_totalPixels + result.TotalPixels);
            _totalChannelDelta += result.MeanChannelDelta * result.TotalPixels * 4;
            _maximumChannelDelta = Math.Max(_maximumChannelDelta, result.MaximumChannelDelta);
        }

        public ImagePlaneComparison ToReport() => new(
            _identicalPixels,
            _totalPixels,
            _maximumChannelDelta,
            _totalChannelDelta / (_totalPixels * 4));
    }

    private sealed record RlesBundleManifest(
        int SchemaVersion,
        string Codec,
        string SourceTgi,
        int SourceWidth,
        int SourceHeight,
        int Width,
        int Height,
        int MipLevel,
        int MipCount,
        string ColorFile,
        string ColorFormat,
        string MaskFile,
        string MaskFormat);

    private sealed record RlesBundleInput(
        string BundlePath,
        RlesBundleManifest Manifest,
        string ColorPngPath,
        string MaskPngPath,
        byte[] ColorPngBytes,
        byte[] MaskPngBytes);
}
