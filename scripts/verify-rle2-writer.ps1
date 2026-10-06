param(
    [string] $FfmpegPath = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cliProject = Join-Path $repoRoot 'src\S4MT.Cli'
if ([string]::IsNullOrWhiteSpace($FfmpegPath)) {
    $ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($null -eq $ffmpeg) {
        throw 'FFmpeg is required as an independent BC3 pixel oracle. Pass -FfmpegPath or install ffmpeg.'
    }
    $FfmpegPath = $ffmpeg.Source
}
if (-not (Test-Path -LiteralPath $FfmpegPath -PathType Leaf)) {
    throw "FFmpeg executable does not exist: $FfmpegPath"
}
$ffmpegVersionOutput = & $FfmpegPath -version 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "FFmpeg failed to report its version: $($ffmpegVersionOutput -join [Environment]::NewLine)"
}
$ffmpegVersion = [string]($ffmpegVersionOutput | Select-Object -First 1)

function Invoke-S4mtJson {
    param([string[]] $Arguments, [int] $ExpectedExitCode = 0)
    $lines = & dotnet run --no-build --project $cliProject -- @Arguments 2>&1
    $exit = $LASTEXITCODE
    if ($exit -ne $ExpectedExitCode) {
        throw "S4MT exited $exit instead of $ExpectedExitCode`: $($lines -join [Environment]::NewLine)"
    }
    return (($lines | Out-String) | ConvertFrom-Json)
}

function Write-PngUInt32BE {
    param([uint32] $Value)
    $bytes = [BitConverter]::GetBytes($Value)
    [Array]::Reverse($bytes)
    return ,$bytes
}

function Get-PngCrc32 {
    param([byte[]] $Bytes)
    [uint32] $crc = [uint32]::MaxValue
    [uint32] $polynomial = [uint32]::Parse('3988292384')
    foreach ($value in $Bytes) {
        $crc = [uint32]($crc -bxor [uint32]$value)
        for ($bit = 0; $bit -lt 8; $bit++) {
            if (($crc -band [uint32]1) -ne 0) {
                $crc = [uint32](($crc -shr 1) -bxor $polynomial)
            }
            else {
                $crc = [uint32]($crc -shr 1)
            }
        }
    }
    return [uint32]($crc -bxor [uint32]::MaxValue)
}

function New-PngChunk {
    param([string] $Type, [byte[]] $Data)
    [byte[]] $typeBytes = [Text.Encoding]::ASCII.GetBytes($Type)
    [byte[]] $crcInput = [byte[]]::new($typeBytes.Length + $Data.Length)
    [Array]::Copy($typeBytes, 0, $crcInput, 0, $typeBytes.Length)
    [Array]::Copy($Data, 0, $crcInput, $typeBytes.Length, $Data.Length)
    [byte[]] $chunk = [byte[]]::new(12 + $Data.Length)
    [byte[]] $lengthBytes = Write-PngUInt32BE $Data.Length
    [Array]::Copy($lengthBytes, 0, $chunk, 0, 4)
    [Array]::Copy($typeBytes, 0, $chunk, 4, 4)
    [Array]::Copy($Data, 0, $chunk, 8, $Data.Length)
    [byte[]] $crcBytes = Write-PngUInt32BE (Get-PngCrc32 $crcInput)
    [Array]::Copy($crcBytes, 0, $chunk, 8 + $Data.Length, 4)
    return ,$chunk
}

function New-TestPng {
    param(
        [string] $Path,
        [int] $Width,
        [int] $Height,
        [ValidateSet('gradient', 'transparent', 'opaque')]
        [string] $Pattern = 'gradient'
    )
    [byte[]] $raw = [byte[]]::new($Height * (1 + $Width * 4))
    $cursor = 0
    for ($y = 0; $y -lt $Height; $y++) {
        $raw[$cursor++] = 0
        for ($x = 0; $x -lt $Width; $x++) {
            if ($Pattern -eq 'gradient') {
                $red = $x * 5 + $y * 2
                $green = $x * 3 + $y * 8
                $blue = 32 + $x * 4 + $y * 5
                $alpha = 255 - $x * 3 - $y * 7
            }
            else {
                $red = 80
                $green = 120
                $blue = 200
                $alpha = if ($Pattern -eq 'transparent') { 0 } else { 255 }
            }
            $raw[$cursor++] = [byte]$red
            $raw[$cursor++] = [byte]$green
            $raw[$cursor++] = [byte]$blue
            $raw[$cursor++] = [byte]$alpha
        }
    }

    $compressed = [System.IO.MemoryStream]::new()
    $zlib = [System.IO.Compression.ZLibStream]::new(
        $compressed,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $true)
    $zlib.Write($raw, 0, $raw.Length)
    $zlib.Dispose()
    [byte[]] $ihdr = [byte[]]::new(13)
    [Array]::Copy((Write-PngUInt32BE $Width), 0, $ihdr, 0, 4)
    [Array]::Copy((Write-PngUInt32BE $Height), 0, $ihdr, 4, 4)
    $ihdr[8] = 8
    $ihdr[9] = 6

    $png = [System.IO.MemoryStream]::new()
    [byte[]] $signature = [byte[]]@(137, 80, 78, 71, 13, 10, 26, 10)
    $png.Write($signature, 0, $signature.Length)
    foreach ($chunk in @(
        (New-PngChunk 'IHDR' $ihdr),
        (New-PngChunk 'IDAT' $compressed.ToArray()),
        (New-PngChunk 'IEND' ([byte[]]::new(0))))) {
        $png.Write($chunk, 0, $chunk.Length)
    }
    [System.IO.File]::WriteAllBytes($Path, $png.ToArray())
    $png.Dispose()
    $compressed.Dispose()
}

function Set-DdsUInt32 {
    param([byte[]] $Header, [int] $Offset, [uint32] $Value)
    [Array]::Copy([BitConverter]::GetBytes($Value), 0, $Header, $Offset, 4)
}

function Copy-PayloadBytes {
    param([byte[]] $Payload, [int] $SourceOffset, [byte[]] $Destination, [int] $DestinationOffset, [int] $Count)
    if ($SourceOffset -lt 0 -or $Count -lt 0 -or $SourceOffset -gt $Payload.Length - $Count -or
        $DestinationOffset -lt 0 -or $DestinationOffset -gt $Destination.Length - $Count) {
        throw "The independent writer oracle encountered an invalid range: source=$SourceOffset+$Count/$($Payload.Length), destination=$DestinationOffset+$Count/$($Destination.Length)."
    }
    [Array]::Copy($Payload, $SourceOffset, $Destination, $DestinationOffset, $Count)
}

function New-Rle2MipDds {
    param([byte[]] $Payload, [int] $MipLevel, [string] $OutputPath)
    if ($Payload.Length -lt 16 -or [Text.Encoding]::ASCII.GetString($Payload, 0, 8) -ne 'DXT5RLE2') {
        throw 'The independent writer oracle expected a DXT5 RLE2 payload.'
    }
    $baseWidth = [BitConverter]::ToUInt16($Payload, 8)
    $baseHeight = [BitConverter]::ToUInt16($Payload, 10)
    $mipCount = [BitConverter]::ToUInt16($Payload, 12)
    $tableEnd = 16 + $mipCount * 5 * 4
    if ($MipLevel -lt 0 -or $MipLevel -ge $mipCount -or $Payload.Length -lt $tableEnd) {
        throw "Mip $MipLevel is outside the independent oracle's $mipCount mips or its table is truncated."
    }

    [int[]] $offsets = [int[]]::new($mipCount * 5)
    for ($mip = 0; $mip -lt $mipCount; $mip++) {
        for ($stream = 0; $stream -lt 5; $stream++) {
            $offsets[$mip * 5 + $stream] = [int][BitConverter]::ToUInt32($Payload, 16 + ($mip * 5 + $stream) * 4)
        }
    }
    [int[]] $starts = [int[]]::new(5)
    [int[]] $ends = [int[]]::new(5)
    for ($stream = 0; $stream -lt 5; $stream++) {
        $starts[$stream] = $offsets[$MipLevel * 5 + $stream]
        $ends[$stream] = if ($MipLevel + 1 -lt $mipCount) {
            $offsets[($MipLevel + 1) * 5 + $stream]
        }
        elseif ($stream -lt 4) {
            $offsets[$stream + 1]
        }
        else {
            $Payload.Length
        }
        if ($starts[$stream] -lt $tableEnd -or $starts[$stream] -gt $ends[$stream] -or
            $ends[$stream] -gt $Payload.Length) {
            throw "The independent writer oracle found an invalid mip $MipLevel stream $stream range."
        }
    }

    $width = [int]$baseWidth
    $height = [int]$baseHeight
    for ($level = 0; $level -lt $MipLevel; $level++) {
        $width = [Math]::Max(1, [int][Math]::Floor($width / 2))
        $height = [Math]::Max(1, [int][Math]::Floor($height / 2))
    }
    $blocksWide = [int][Math]::Ceiling($width / 4.0)
    $blocksHigh = [int][Math]::Ceiling($height / 4.0)
    $expectedBlocks = $blocksWide * $blocksHigh
    [byte[]] $blocks = [byte[]]::new($expectedBlocks * 16)
    $commandCursor = $starts[0]
    $blockIndex = 0
    while ($commandCursor -lt $ends[0]) {
        if ($ends[0] - $commandCursor -lt 2) {
            throw 'The independent writer oracle found a truncated command.'
        }
        $command = [BitConverter]::ToUInt16($Payload, $commandCursor)
        $commandCursor += 2
        $opcode = $command -band 3
        $count = $command -shr 2
        if ($opcode -ne 1 -or $count -eq 0 -or $blockIndex + $count -gt $expectedBlocks) {
            throw 'The independent writer oracle expected nonempty opcode-1 runs covering the mip.'
        }
        for ($run = 0; $run -lt $count; $run++) {
            $destination = $blockIndex * 16
            Copy-PayloadBytes $Payload $starts[3] $blocks $destination 2
            Copy-PayloadBytes $Payload $starts[4] $blocks ($destination + 2) 6
            Copy-PayloadBytes $Payload $starts[1] $blocks ($destination + 8) 4
            Copy-PayloadBytes $Payload $starts[2] $blocks ($destination + 12) 4
            $starts[1] += 4
            $starts[2] += 4
            $starts[3] += 2
            $starts[4] += 6
            $blockIndex++
        }
    }
    if ($blockIndex -ne $expectedBlocks -or $commandCursor -ne $ends[0] -or
        $starts[1] -ne $ends[1] -or $starts[2] -ne $ends[2] -or
        $starts[3] -ne $ends[3] -or $starts[4] -ne $ends[4]) {
        throw "The independent writer oracle did not consume exactly $expectedBlocks blocks and all four component streams."
    }

    [byte[]] $header = [byte[]]::new(128)
    [Array]::Copy([Text.Encoding]::ASCII.GetBytes('DDS '), 0, $header, 0, 4)
    Set-DdsUInt32 $header 4 124
    Set-DdsUInt32 $header 8 0x81007
    Set-DdsUInt32 $header 12 $height
    Set-DdsUInt32 $header 16 $width
    Set-DdsUInt32 $header 20 ($expectedBlocks * 16)
    Set-DdsUInt32 $header 28 1
    Set-DdsUInt32 $header 76 32
    Set-DdsUInt32 $header 80 4
    [Array]::Copy([Text.Encoding]::ASCII.GetBytes('DXT5'), 0, $header, 84, 4)
    Set-DdsUInt32 $header 108 0x1000
    [byte[]] $dds = [byte[]]::new($header.Length + $blocks.Length)
    [Array]::Copy($header, 0, $dds, 0, $header.Length)
    [Array]::Copy($blocks, 0, $dds, $header.Length, $blocks.Length)
    [System.IO.File]::WriteAllBytes($OutputPath, $dds)
    return [pscustomobject]@{ width = $width; height = $height; mipCount = $mipCount }
}

function Convert-ToRawRgba {
    param([string] $InputPath, [string] $OutputPath)
    $lines = & $FfmpegPath -y -hide_banner -loglevel error -i $InputPath -frames:v 1 -f rawvideo -pix_fmt rgba $OutputPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "FFmpeg could not decode '$InputPath': $($lines -join [Environment]::NewLine)"
    }
}

function Assert-BytesEqual {
    param([byte[]] $Expected, [byte[]] $Actual, [string] $Description)
    if ($Expected.Length -ne $Actual.Length) {
        throw "$Description length differs: expected $($Expected.Length), got $($Actual.Length)."
    }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Expected[$index] -ne $Actual[$index]) {
            throw "$Description differs at byte ${index}: expected $($Expected[$index]), got $($Actual[$index])."
        }
    }
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('s4mt-rle2-writer-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    $pngPath = Join-Path $tempRoot 'source.png'
    $builtPackage = Join-Path $tempRoot 'built.package'
    $sourcePackage = Join-Path $tempRoot 'source.package'
    $importedPackage = Join-Path $tempRoot 'imported.package'
    $payloadPath = Join-Path $tempRoot 'rle2.bin'
    $sourceTgi = '3453CF95:00000000:0000000000000001'
    $sentinelTgi = '220557DA:00000000:0000000000000002'
    $width = 32
    $height = 16
    New-TestPng $pngPath $width $height

    $build = Invoke-S4mtJson @(
        'image', 'build', '--tgi', $sourceTgi, '--png', $pngPath, '--output', $builtPackage)
    if (-not $build.readBackVerified -or $build.width -ne $width -or $build.height -ne $height -or
        $build.totalPixels -ne ($width * $height)) {
        throw 'RLE2 image build did not verify the generated mip-0 read-back and dimensions.'
    }
    $null = Invoke-S4mtJson @(
        'resource', 'export', '--package', $builtPackage, '--tgi', $sourceTgi, '--output', $payloadPath)
    [byte[]] $payload = [System.IO.File]::ReadAllBytes($payloadPath)
    $mipCount = [BitConverter]::ToUInt16($payload, 12)
    if ([Text.Encoding]::ASCII.GetString($payload, 0, 8) -ne 'DXT5RLE2' -or
        [BitConverter]::ToUInt16($payload, 8) -ne $width -or
        [BitConverter]::ToUInt16($payload, 10) -ne $height -or $mipCount -ne 6) {
        throw "RLE2 payload header differs from the expected $width x $height full mip chain."
    }

    $mipEvidence = [System.Collections.Generic.List[object]]::new()
    $inputRawPath = Join-Path $tempRoot 'source.rgba'
    Convert-ToRawRgba $pngPath $inputRawPath
    [byte[]] $inputPixels = [System.IO.File]::ReadAllBytes($inputRawPath)
    [byte[]] $mipZeroPixels = [byte[]]::new(0)
    $mipWidth = $width
    $mipHeight = $height
    for ($mip = 0; $mip -lt $mipCount; $mip++) {
        $pngOutput = Join-Path $tempRoot "mip-$mip.png"
        $ddsPath = Join-Path $tempRoot "mip-$mip.dds"
        $pngRawPath = Join-Path $tempRoot "mip-$mip-png.rgba"
        $ddsRawPath = Join-Path $tempRoot "mip-$mip-dds.rgba"
        $report = Invoke-S4mtJson @(
            'image', 'export', '--package', $builtPackage, '--tgi', $sourceTgi,
            '--output', $pngOutput, '--mip', [string]$mip)
        if ($report.mipLevel -ne $mip -or $report.mipCount -ne $mipCount -or
            $report.width -ne $mipWidth -or $report.height -ne $mipHeight) {
            throw "RLE2 mip $mip export metadata differs from $mipWidth x $mipHeight."
        }
        $oracle = New-Rle2MipDds $payload $mip $ddsPath
        Convert-ToRawRgba $ddsPath $ddsRawPath
        Convert-ToRawRgba $pngOutput $pngRawPath
        [byte[]] $actualPixels = [System.IO.File]::ReadAllBytes($pngRawPath)
        [byte[]] $oraclePixels = [System.IO.File]::ReadAllBytes($ddsRawPath)
        Assert-BytesEqual $oraclePixels $actualPixels "RLE2 mip $mip RGBA against FFmpeg"
        if ($mip -eq 0) {
            $mipZeroPixels = $actualPixels
        }
        $mipEvidence.Add([pscustomobject]@{
            mipLevel = $mip
            width = $mipWidth
            height = $mipHeight
            rgbaBytes = $actualPixels.Length
            ffmpegMatch = $true
        })
        $mipWidth = [Math]::Max(1, [int][Math]::Floor($mipWidth / 2))
        $mipHeight = [Math]::Max(1, [int][Math]::Floor($mipHeight / 2))
    }

    $channelQuality = @(
        for ($channel = 0; $channel -lt 4; $channel++) {
            $maximum = 0
            $total = 0L
            for ($offset = $channel; $offset -lt $inputPixels.Length; $offset += 4) {
                $delta = [Math]::Abs([int]$inputPixels[$offset] - [int]$mipZeroPixels[$offset])
                $maximum = [Math]::Max($maximum, $delta)
                $total += $delta
            }
            [pscustomobject]@{
                channel = @('R', 'G', 'B', 'A')[$channel]
                maximumDelta = $maximum
                meanDelta = $total / ($inputPixels.Length / 4)
            }
        }
    )
    $qualityMaximum = ($channelQuality | Measure-Object -Property maximumDelta -Maximum).Maximum
    $qualityMean = ($channelQuality | Measure-Object -Property meanDelta -Sum).Sum / 4
    if ($qualityMaximum -ne $build.maximumChannelDelta -or
        [Math]::Abs($qualityMean - $build.meanChannelDelta) -gt 0.000001) {
        throw 'The RLE2 build report differs from the independently measured mip-0 pixel deltas.'
    }
    if ($qualityMaximum -gt 16 -or $qualityMean -gt 4) {
        throw "The deterministic gradient BC3 quality probe exceeded its limit: max=$qualityMaximum mean=$qualityMean."
    }

    [byte[]] $sentinel = [byte[]]@(7, 13, 42, 99, 255)
    $sentinelPayload = Join-Path $tempRoot 'sentinel.bin'
    [System.IO.File]::WriteAllBytes($sentinelPayload, $sentinel)
    $sentinelManifestPath = Join-Path $tempRoot 'sentinel-manifest.json'
    $sentinelManifest = [ordered]@{ format = 1; resources = @(
        [ordered]@{
            type = '0x220557DA'
            group = '0x00000000'
            instance = '0x0000000000000002'
            source = 'sentinel.bin'
            encoding = 'bytes'
        }
    ) }
    [System.IO.File]::WriteAllText(
        $sentinelManifestPath,
        ($sentinelManifest | ConvertTo-Json -Depth 5),
        [System.Text.UTF8Encoding]::new($false))
    $sentinelPackage = Join-Path $tempRoot 'sentinel.package'
    $null = Invoke-S4mtJson @('build', '--manifest', $sentinelManifestPath, '--output', $sentinelPackage)
    $mergeManifestPath = Join-Path $tempRoot 'merge.json'
    $mergeManifest = [ordered]@{ format = 1; packages = @('built.package', 'sentinel.package') }
    [System.IO.File]::WriteAllText(
        $mergeManifestPath,
        ($mergeManifest | ConvertTo-Json -Depth 5),
        [System.Text.UTF8Encoding]::new($false))
    $null = Invoke-S4mtJson @(
        'package', 'merge', '--manifest', $mergeManifestPath, '--output', $sourcePackage)
    $import = Invoke-S4mtJson @(
        'image', 'import', '--package', $sourcePackage, '--tgi', $sourceTgi,
        '--png', $pngPath, '--output', $importedPackage)
    if (-not $import.readBackVerified -or $import.packageResourceCount -ne 2) {
        throw 'RLE2 image import did not verify read-back while retaining both package resources.'
    }

    $sentinelBefore = Join-Path $tempRoot 'sentinel-before.bin'
    $sentinelAfter = Join-Path $tempRoot 'sentinel-after.bin'
    $null = Invoke-S4mtJson @(
        'resource', 'export', '--package', $sourcePackage, '--tgi', $sentinelTgi, '--output', $sentinelBefore)
    $null = Invoke-S4mtJson @(
        'resource', 'export', '--package', $importedPackage, '--tgi', $sentinelTgi, '--output', $sentinelAfter)
    Assert-BytesEqual $sentinel $([System.IO.File]::ReadAllBytes($sentinelBefore)) 'Sentinel source payload'
    Assert-BytesEqual $sentinel $([System.IO.File]::ReadAllBytes($sentinelAfter)) 'Sentinel imported payload'
    $importedPayloadPath = Join-Path $tempRoot 'imported-rle2.bin'
    $null = Invoke-S4mtJson @(
        'resource', 'export', '--package', $importedPackage, '--tgi', $sourceTgi, '--output', $importedPayloadPath)
    Assert-BytesEqual $payload $([System.IO.File]::ReadAllBytes($importedPayloadPath)) 'RLE2 payload after import'

    $edgeEvidence = [System.Collections.Generic.List[object]]::new()
    foreach ($pattern in @('transparent', 'opaque')) {
        $edgeTgi = if ($pattern -eq 'transparent') {
            '3453CF95:00000000:0000000000000003'
        }
        else {
            '3453CF95:00000000:0000000000000004'
        }
        $edgePng = Join-Path $tempRoot "$pattern.png"
        $edgePackage = Join-Path $tempRoot "$pattern.package"
        $edgePayloadPath = Join-Path $tempRoot "$pattern.bin"
        $edgeExportPath = Join-Path $tempRoot "$pattern-export.png"
        $edgeDdsPath = Join-Path $tempRoot "$pattern.dds"
        $edgePngRawPath = Join-Path $tempRoot "$pattern-png.rgba"
        $edgeDdsRawPath = Join-Path $tempRoot "$pattern-dds.rgba"
        New-TestPng $edgePng 1 1 $pattern
        $edgeBuild = Invoke-S4mtJson @(
            'image', 'build', '--tgi', $edgeTgi, '--png', $edgePng, '--output', $edgePackage)
        if (-not $edgeBuild.readBackVerified -or $edgeBuild.width -ne 1 -or $edgeBuild.height -ne 1) {
            throw "RLE2 $pattern edge-case build did not verify its 1 x 1 read-back."
        }
        $null = Invoke-S4mtJson @(
            'resource', 'export', '--package', $edgePackage, '--tgi', $edgeTgi, '--output', $edgePayloadPath)
        [byte[]] $edgePayload = [System.IO.File]::ReadAllBytes($edgePayloadPath)
        if ([BitConverter]::ToUInt16($edgePayload, 12) -ne 1) {
            throw "RLE2 $pattern edge-case payload should contain one mip."
        }
        $edgeReport = Invoke-S4mtJson @(
            'image', 'export', '--package', $edgePackage, '--tgi', $edgeTgi, '--output', $edgeExportPath)
        if ($edgeReport.mipLevel -ne 0 -or $edgeReport.width -ne 1 -or $edgeReport.height -ne 1) {
            throw "RLE2 $pattern default export did not select its single mip."
        }
        $null = New-Rle2MipDds $edgePayload 0 $edgeDdsPath
        Convert-ToRawRgba $edgeDdsPath $edgeDdsRawPath
        Convert-ToRawRgba $edgeExportPath $edgePngRawPath
        Assert-BytesEqual $([System.IO.File]::ReadAllBytes($edgeDdsRawPath)) `
            $([System.IO.File]::ReadAllBytes($edgePngRawPath)) "RLE2 $pattern edge-case RGBA against FFmpeg"
        $edgeEvidence.Add([pscustomobject]@{
            pattern = $pattern
            width = 1
            height = 1
            oneMip = $true
            defaultMipExport = $true
            ffmpegMatch = $true
        })
    }

    $verification = [pscustomobject]@{
        result = 'verified'
        ffmpegVersion = $ffmpegVersion
        input = [pscustomobject]@{ width = $width; height = $height }
        resource = $sourceTgi
        fullMipCount = $mipCount
        mipResults = @($mipEvidence)
        edgeCases = @($edgeEvidence)
        buildReadBackVerified = $build.readBackVerified
        importReadBackVerified = $import.readBackVerified
        importedPayloadEqualsBuild = $true
        packageResourceCountAfterImport = $import.packageResourceCount
        unrelatedPayloadPreserved = $true
        rgbaRoundtripPixelQuality = [pscustomobject]@{
            identicalPixels = $build.identicalPixels
            totalPixels = $build.totalPixels
            maximumChannelDelta = $build.maximumChannelDelta
            meanChannelDelta = $build.meanChannelDelta
            perChannelMaximumDelta = @($channelQuality | Select-Object channel, maximumDelta, meanDelta)
        }
    }
    $verificationPath = Join-Path $repoRoot '.audit\rle2-writer-verification.json'
    $verification | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $verificationPath -Encoding utf8
    $verification | ConvertTo-Json -Depth 8
}
finally {
    if (Test-Path -LiteralPath $tempRoot -PathType Container) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
