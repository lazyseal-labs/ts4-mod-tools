param(
    [string] $GameRoot = 'C:\Program Files\EA Games\The Sims 4',
    [string] $FfmpegPath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cliProject = Join-Path $repoRoot 'src\S4MT.Cli'
$fixtureManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'rle-dxt5-fixtures.json') -Raw | ConvertFrom-Json
$fixturePackage = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetFullPath($GameRoot)) $fixtureManifest.package))
if (-not (Test-Path -LiteralPath $fixturePackage -PathType Leaf)) { throw "Pinned RLES package missing: $fixturePackage" }
if (-not [BitConverter]::IsLittleEndian) { throw 'The RLES oracle requires a little-endian host.' }
if ([string]::IsNullOrWhiteSpace($FfmpegPath)) {
    $ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($null -eq $ffmpeg) { throw 'FFmpeg is required as an independent BC3 pixel oracle.' }
    $FfmpegPath = $ffmpeg.Source
}
if (-not (Test-Path -LiteralPath $FfmpegPath -PathType Leaf)) { throw "FFmpeg missing: $FfmpegPath" }
$ffmpegVersionOutput = & $FfmpegPath -version 2>&1
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg version probe failed.' }
$ffmpegVersion = [string]($ffmpegVersionOutput | Select-Object -First 1)

function Invoke-S4mtJson {
    param([string[]] $Arguments)
    $lines = & dotnet run --no-build --project $cliProject -- @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "S4MT exited $LASTEXITCODE`: $($lines -join [Environment]::NewLine)" }
    return (($lines | Out-String) | ConvertFrom-Json)
}

function Assert-S4mtFails {
    param([string[]] $Arguments, [string] $ExpectedMessage)
    $lines = & dotnet run --no-build --project $cliProject -- @Arguments 2>&1
    if ($LASTEXITCODE -eq 0) { throw "S4MT unexpectedly accepted: $($Arguments -join ' ')" }
    if (($lines | Out-String) -notmatch $ExpectedMessage) {
        throw "S4MT failed for the wrong reason. Expected /$ExpectedMessage/, got: $($lines -join [Environment]::NewLine)"
    }
}
function Copy-PayloadBytes {
    param([byte[]] $Source, [int] $SourceOffset, [byte[]] $Destination, [int] $DestinationOffset, [int] $Count)
    if ($SourceOffset -lt 0 -or $Count -lt 0 -or $SourceOffset -gt $Source.Length - $Count -or
        $DestinationOffset -lt 0 -or $DestinationOffset -gt $Destination.Length - $Count) {
        throw 'The RLES oracle encountered an invalid component range.'
    }
    [Array]::Copy($Source, $SourceOffset, $Destination, $DestinationOffset, $Count)
}

function Set-DdsUInt32 {
    param([byte[]] $Header, [int] $Offset, [uint32] $Value)
    [Array]::Copy([BitConverter]::GetBytes($Value), 0, $Header, $Offset, 4)
}

function Get-RlesMipOracle {
    param([byte[]] $Payload, [int] $MipLevel, [string] $DdsPath)

    if ($Payload.Length -lt 16 -or [Text.Encoding]::ASCII.GetString($Payload, 0, 8) -ne 'DXT5RLES') {
        throw 'The independent oracle expected a DXT5 RLES payload.'
    }
    $baseWidth = [int][BitConverter]::ToUInt16($Payload, 8)
    $baseHeight = [int][BitConverter]::ToUInt16($Payload, 10)
    $mipCount = [int][BitConverter]::ToUInt16($Payload, 12)
    $tableEnd = 16 + $mipCount * 6 * 4
    if ($baseWidth -lt 1 -or $baseHeight -lt 1 -or $mipCount -lt 1 -or
        $MipLevel -lt 0 -or $MipLevel -ge $mipCount -or $Payload.Length -lt $tableEnd) {
        throw 'RLES fixture header or selected mip is invalid.'
    }

    [int[]] $offsets = [int[]]::new($mipCount * 6)
    for ($mip = 0; $mip -lt $mipCount; $mip++) {
        for ($stream = 0; $stream -lt 6; $stream++) {
            $value = [BitConverter]::ToUInt32($Payload, 16 + ($mip * 6 + $stream) * 4)
            if ($value -gt $Payload.Length) { throw 'RLES component offset exceeds payload length.' }
            $offsets[$mip * 6 + $stream] = [int]$value
        }
    }
    [int[]] $streamStarts = [int[]]::new(6)
    [int[]] $streamEnds = [int[]]::new(6)
    for ($stream = 0; $stream -lt 6; $stream++) {
        $streamStarts[$stream] = $offsets[$stream]
        $streamEnds[$stream] = if ($stream -lt 5) { $offsets[$stream + 1] } else { $Payload.Length }
        if ($streamStarts[$stream] -lt $tableEnd -or $streamStarts[$stream] -gt $streamEnds[$stream] -or
            $streamEnds[$stream] -gt $Payload.Length) {
            throw "RLES component stream $stream has an invalid range."
        }
    }

    $width = $baseWidth
    $height = $baseHeight
    for ($level = 0; $level -lt $MipLevel; $level++) {
        $width = [Math]::Max(1, [int][Math]::Floor($width / 2))
        $height = [Math]::Max(1, [int][Math]::Floor($height / 2))
    }
    $blocksWide = [int][Math]::Ceiling($width / 4.0)
    $blocksHigh = [int][Math]::Ceiling($height / 4.0)
    $expectedBlocks = $blocksWide * $blocksHigh
    [byte[]] $blocks = [byte[]]::new($expectedBlocks * 16)
    [byte[]] $mask = [byte[]]::new($width * $height * 4)
    [int[]] $cursors = [int[]]::new(6)
    [int[]] $ends = [int[]]::new(6)
    for ($stream = 0; $stream -lt 6; $stream++) {
        $cursors[$stream] = $offsets[$MipLevel * 6 + $stream]
        $ends[$stream] = if ($MipLevel + 1 -lt $mipCount) {
            $offsets[($MipLevel + 1) * 6 + $stream]
        } else {
            $streamEnds[$stream]
        }
        if ($cursors[$stream] -lt $streamStarts[$stream] -or $cursors[$stream] -gt $ends[$stream] -or
            $ends[$stream] -gt $streamEnds[$stream]) {
            throw "RLES mip $MipLevel stream $stream has an invalid range."
        }
    }

    [long[]] $opcodeTotals = [long[]]::new(3)
    $commandCursor = $cursors[0]
    while ($commandCursor -lt $ends[0]) {
        if ($ends[0] - $commandCursor -lt 2) { throw 'RLES command stream is truncated.' }
        $command = [BitConverter]::ToUInt16($Payload, $commandCursor)
        $commandCursor += 2
        $opcode = $command -band 3
        $count = $command -shr 2
        if ($opcode -eq 3 -or $count -eq 0) { throw 'RLES command has invalid opcode or run length.' }
        $opcodeTotals[$opcode] += $count
    }
    $texturedBlocks = $opcodeTotals[1] + $opcodeTotals[2]
    if (($opcodeTotals[0] + $texturedBlocks) -ne $expectedBlocks -or
        ($texturedBlocks * 4) -ne ($ends[1] - $cursors[1]) -or
        ($texturedBlocks * 4) -ne ($ends[2] - $cursors[2]) -or
        ($texturedBlocks * 2) -ne ($ends[3] - $cursors[3]) -or
        ($texturedBlocks * 6) -ne ($ends[4] - $cursors[4]) -or
        ($opcodeTotals[1] * 16) -ne ($ends[5] - $cursors[5])) {
        throw "RLES mip $MipLevel command counts differ from component stream lengths."
    }

    $transparentBlock = [byte[]]@(0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
    $commandCursor = $cursors[0]
    $blockIndex = 0
    while ($commandCursor -lt $ends[0]) {
        $command = [BitConverter]::ToUInt16($Payload, $commandCursor)
        $commandCursor += 2
        $opcode = $command -band 3
        $count = $command -shr 2
        for ($run = 0; $run -lt $count; $run++) {
            $destination = $blockIndex * 16
            if ($opcode -eq 0) {
                [Array]::Copy($transparentBlock, 0, $blocks, $destination, 16)
            } else {
                Copy-PayloadBytes $Payload $cursors[3] $blocks $destination 2
                Copy-PayloadBytes $Payload $cursors[4] $blocks ($destination + 2) 6
                Copy-PayloadBytes $Payload $cursors[1] $blocks ($destination + 8) 4
                Copy-PayloadBytes $Payload $cursors[2] $blocks ($destination + 12) 4
                $cursors[3] += 2
                $cursors[4] += 6
                $cursors[1] += 4
                $cursors[2] += 4
            }

            $blockX = $blockIndex % $blocksWide
            $blockY = [int][Math]::Floor($blockIndex / $blocksWide)
            for ($localY = 0; $localY -lt 4; $localY++) {
                $y = $blockY * 4 + $localY
                if ($y -ge $height) { continue }
                for ($localX = 0; $localX -lt 4; $localX++) {
                    $x = $blockX * 4 + $localX
                    if ($x -ge $width) { continue }
                    $value = if ($opcode -eq 0) { 0 } elseif ($opcode -eq 2) { 255 } else {
                        $Payload[$cursors[5] + $localY * 4 + $localX]
                    }
                    $pixelOffset = ($y * $width + $x) * 4
                    $mask[$pixelOffset] = [byte]$value
                    $mask[$pixelOffset + 1] = [byte]$value
                    $mask[$pixelOffset + 2] = [byte]$value
                    $mask[$pixelOffset + 3] = 255
                }
            }
            if ($opcode -eq 1) { $cursors[5] += 16 }
            $blockIndex++
        }
    }
    if ($blockIndex -ne $expectedBlocks) { throw "RLES oracle decoded $blockIndex blocks instead of $expectedBlocks." }
    for ($stream = 1; $stream -lt 6; $stream++) {
        if ($cursors[$stream] -ne $ends[$stream]) { throw "RLES oracle did not fully consume stream $stream at mip $MipLevel." }
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
    [System.IO.File]::WriteAllBytes($DdsPath, $dds)
    return [pscustomobject]@{
        baseWidth = $baseWidth
        baseHeight = $baseHeight
        width = $width
        height = $height
        mipCount = $mipCount
        maskRgba = $mask
        opcodeTotals = $opcodeTotals
    }
}

function Convert-ToRawRgba {
    param([string] $InputPath, [string] $OutputPath)
    $lines = & $FfmpegPath -y -hide_banner -loglevel error -i $InputPath -frames:v 1 -f rawvideo -pix_fmt rgba $OutputPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "FFmpeg could not decode '$InputPath': $($lines -join [Environment]::NewLine)"
    }
}

function Assert-EqualBytes {
    param([byte[]] $Actual, [byte[]] $Expected, [string] $Label)
    if ($Actual.Length -ne $Expected.Length) {
        throw "$Label byte count differs: actual=$($Actual.Length), expected=$($Expected.Length)."
    }
    for ($index = 0; $index -lt $Expected.Length; $index++) {
        if ($Actual[$index] -ne $Expected[$index]) {
            throw "$Label differs at RGBA byte $index`: actual=$($Actual[$index]), expected=$($Expected[$index])."
        }
    }
}

function Normalize-Tgi {
    param([string] $Tgi)
    return (($Tgi -split ':') | ForEach-Object { $_.Replace('0x', '').Replace('0X', '').ToUpperInvariant() }) -join ':'
}

function Write-RawRgbaPng {
    param([byte[]] $Pixels, [int] $Width, [int] $Height, [string] $OutputPath, [string] $RawPath)
    [System.IO.File]::WriteAllBytes($RawPath, $Pixels)
    $lines = & $FfmpegPath -y -hide_banner -loglevel error -f rawvideo -pixel_format rgba `
        -video_size "${Width}x${Height}" -i $RawPath -frames:v 1 $OutputPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "FFmpeg could not encode the invalid-mask probe: $($lines -join [Environment]::NewLine)"
    }
}

function Assert-FileUnchanged {
    param([string] $Path, [byte[]] $Expected, [string] $Label)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "$Label destination disappeared." }
    Assert-EqualBytes ([System.IO.File]::ReadAllBytes($Path)) $Expected $Label
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('s4mt-rles-writer-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    $fixture = @($fixtureManifest.fixtures | Where-Object type -eq 'rles' | Select-Object -First 1)[0]
    if ($null -eq $fixture) { throw 'Pinned RLES fixture list is empty.' }
    $tgi = [string]$fixture.tgi
    $fixturePayloadPath = Join-Path $tempRoot 'fixture.bin'
    $null = Invoke-S4mtJson @('resource', 'export', '--package', $fixturePackage,
        '--tgi', $tgi, '--output', $fixturePayloadPath)
    if ((Get-FileHash -LiteralPath $fixturePayloadPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $fixture.sha256) {
        throw 'Pinned RLES fixture SHA-256 changed.'
    }

    $baseBundle = Join-Path $tempRoot 'base.bundle'
    $export = Invoke-S4mtJson @('image', 'export', '--package', $fixturePackage,
        '--tgi', $tgi, '--bundle', $baseBundle)
    $manifest = Get-Content -LiteralPath (Join-Path $baseBundle 'manifest.json') -Raw | ConvertFrom-Json
    $width = [int]$manifest.width
    $height = [int]$manifest.height
    $expectedMipCount = 1
    $maxSide = [Math]::Max($width, $height)
    while ($maxSide -gt 1) {
        $expectedMipCount++
        $maxSide = [Math]::Max(1, [int][Math]::Floor($maxSide / 2))
    }
    if ($manifest.mipLevel -ne 0 -or $manifest.mipCount -ne $expectedMipCount -or
        $export.width -ne $width -or $export.height -ne $height -or
        $width -ne [int][BitConverter]::ToUInt16(([System.IO.File]::ReadAllBytes($fixturePayloadPath)), 8) -or
        $height -ne [int][BitConverter]::ToUInt16(([System.IO.File]::ReadAllBytes($fixturePayloadPath)), 10)) {
        throw 'The pinned base bundle has unexpected dimensions or mip metadata.'
    }

    $builtPackage = Join-Path $tempRoot 'built.package'
    $build = Invoke-S4mtJson @('image', 'build', '--tgi', $tgi,
        '--bundle', $baseBundle, '--output', $builtPackage)
    if (-not $build.readBackVerified -or -not $build.allMipsDecoded -or
        $build.pixelVerifiedMipCount -ne $expectedMipCount -or
        $build.mipCount -ne $expectedMipCount -or $build.width -ne $width -or $build.height -ne $height -or
        $build.mask.maximumChannelDelta -ne 0 -or $build.mask.meanChannelDelta -ne 0 -or
        $build.mask.identicalPixels -ne $build.mask.totalPixels) {
        throw 'RLES build did not report every mip decoded and pixel-verified with an exact mask.'
    }
    $payloadPath = Join-Path $tempRoot 'built-rles.bin'
    $null = Invoke-S4mtJson @('resource', 'export', '--package', $builtPackage,
        '--tgi', $tgi, '--output', $payloadPath)
    [byte[]] $payload = [System.IO.File]::ReadAllBytes($payloadPath)
    if ([Text.Encoding]::ASCII.GetString($payload, 0, 8) -ne 'DXT5RLES' -or
        [BitConverter]::ToUInt16($payload, 8) -ne $width -or
        [BitConverter]::ToUInt16($payload, 10) -ne $height -or
        [BitConverter]::ToUInt16($payload, 12) -ne $expectedMipCount) {
        throw 'Generated RLES header differs from the source dimensions or complete mip chain.'
    }

    $mipEvidence = [System.Collections.Generic.List[object]]::new()
    [long[]] $opcodeTotals = [long[]]::new(3)
    for ($mip = 0; $mip -lt $expectedMipCount; $mip++) {
        $name = "mip-$mip"
        $ddsPath = Join-Path $tempRoot "$name.dds"
        $oracleColorPath = Join-Path $tempRoot "$name-oracle.rgba"
        $actualColorPath = Join-Path $tempRoot "$name-color.rgba"
        $actualMaskPath = Join-Path $tempRoot "$name-mask.rgba"
        $bundlePath = Join-Path $tempRoot "$name.bundle"
        $oracle = Get-RlesMipOracle $payload $mip $ddsPath
        $report = Invoke-S4mtJson @('image', 'export', '--package', $builtPackage,
            '--tgi', $tgi, '--bundle', $bundlePath, '--mip', [string]$mip)
        if ($report.width -ne $oracle.width -or $report.height -ne $oracle.height -or
            $report.mipLevel -ne $mip -or $report.mipCount -ne $expectedMipCount) {
            throw "Generated mip $mip export metadata differs from the independent oracle."
        }
        $mipManifest = Get-Content -LiteralPath (Join-Path $bundlePath 'manifest.json') -Raw | ConvertFrom-Json
        if ($mipManifest.width -ne $oracle.width -or $mipManifest.height -ne $oracle.height -or
            $mipManifest.mipLevel -ne $mip -or $mipManifest.mipCount -ne $expectedMipCount) {
            throw "Generated mip $mip bundle metadata differs from the independent oracle."
        }
        Convert-ToRawRgba $ddsPath $oracleColorPath
        Convert-ToRawRgba (Join-Path $bundlePath 'color.png') $actualColorPath
        Convert-ToRawRgba (Join-Path $bundlePath 'mask.png') $actualMaskPath
        Assert-EqualBytes ([System.IO.File]::ReadAllBytes($actualColorPath)) `
            ([System.IO.File]::ReadAllBytes($oracleColorPath)) "RLES mip $mip FFmpeg color"
        Assert-EqualBytes ([System.IO.File]::ReadAllBytes($actualMaskPath)) `
            $oracle.maskRgba "RLES mip $mip independent mask"
        for ($opcode = 0; $opcode -lt 3; $opcode++) { $opcodeTotals[$opcode] += $oracle.opcodeTotals[$opcode] }
        $mipEvidence.Add([pscustomobject]@{
            mipLevel = $mip
            width = $oracle.width
            height = $oracle.height
            ffmpegColorMatch = $true
            independentMaskMatch = $true
            opcodeBlocks = @($oracle.opcodeTotals)
        })
    }
    if ($opcodeTotals[1] -eq 0) { throw 'Generated RLES payload has no opcode-1 mask blocks.' }

    [byte[]] $sentinel = [byte[]]@(7, 13, 42, 99, 255)
    $sentinelTgi = '220557DA:00000000:0000000000000002'
    $sentinelPath = Join-Path $tempRoot 'sentinel.bin'
    [System.IO.File]::WriteAllBytes($sentinelPath, $sentinel)
    $sentinelManifestPath = Join-Path $tempRoot 'sentinel-manifest.json'
    [ordered]@{ format = 1; resources = @([ordered]@{
        type = '0x220557DA'; group = '0x00000000'; instance = '0x0000000000000002';
        source = 'sentinel.bin'; encoding = 'bytes'
    }) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $sentinelManifestPath -Encoding utf8
    $sentinelPackage = Join-Path $tempRoot 'sentinel.package'
    $null = Invoke-S4mtJson @('build', '--manifest', $sentinelManifestPath, '--output', $sentinelPackage)
    $mergeManifestPath = Join-Path $tempRoot 'merge.json'
    [ordered]@{ format = 1; packages = @('built.package', 'sentinel.package') } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $mergeManifestPath -Encoding utf8
    $sourcePackage = Join-Path $tempRoot 'source.package'
    $null = Invoke-S4mtJson @('package', 'merge', '--manifest', $mergeManifestPath,
        '--output', $sourcePackage)
    $importedPackage = Join-Path $tempRoot 'imported.package'
    $import = Invoke-S4mtJson @('image', 'import', '--package', $sourcePackage,
        '--tgi', $tgi, '--bundle', $baseBundle, '--output', $importedPackage)
    if (-not $import.readBackVerified -or -not $import.allMipsDecoded -or
        $import.pixelVerifiedMipCount -ne $expectedMipCount -or
        $import.packageResourceCount -ne 2 -or
        $import.mask.maximumChannelDelta -ne 0 -or $import.mask.meanChannelDelta -ne 0 -or
        $import.mask.identicalPixels -ne $import.mask.totalPixels) {
        throw 'RLES import did not preserve and pixel-verify the complete package.'
    }
    $importedPayloadPath = Join-Path $tempRoot 'imported-rles.bin'
    $importedSentinelPath = Join-Path $tempRoot 'imported-sentinel.bin'
    $null = Invoke-S4mtJson @('resource', 'export', '--package', $importedPackage,
        '--tgi', $tgi, '--output', $importedPayloadPath)
    $null = Invoke-S4mtJson @('resource', 'export', '--package', $importedPackage,
        '--tgi', $sentinelTgi, '--output', $importedSentinelPath)
    Assert-EqualBytes ([System.IO.File]::ReadAllBytes($importedPayloadPath)) $payload 'RLES import payload'
    Assert-EqualBytes ([System.IO.File]::ReadAllBytes($importedSentinelPath)) $sentinel 'RLES unrelated resource'

    [byte[]] $sourcePackageBefore = [System.IO.File]::ReadAllBytes($sourcePackage)
    $maskRawPath = Join-Path $tempRoot 'base-mask.rgba'
    Convert-ToRawRgba (Join-Path $baseBundle 'mask.png') $maskRawPath
    [byte[]] $baseMaskPixels = [System.IO.File]::ReadAllBytes($maskRawPath)
    $negativeCases = [System.Collections.Generic.List[string]]::new()
    foreach ($kind in @('nongrayscale', 'transparent')) {
        $badBundle = Join-Path $tempRoot "$kind.bundle"
        Copy-Item -LiteralPath $baseBundle -Destination $badBundle -Recurse
        [byte[]] $badPixels = [System.IO.File]::ReadAllBytes($maskRawPath)
        if ($kind -eq 'nongrayscale') { $badPixels[0] = [byte](255 - [int]$badPixels[1]) }
        else { $badPixels[3] = 254 }
        Write-RawRgbaPng $badPixels $width $height (Join-Path $badBundle 'mask.png') `
            (Join-Path $tempRoot "$kind.raw")
        $failedOutput = Join-Path $tempRoot "$kind-failed.package"
        [byte[]] $marker = [byte[]]@(11, 22, 33, 44)
        [System.IO.File]::WriteAllBytes($failedOutput, $marker)
        Assert-S4mtFails @('image', 'build', '--tgi', $tgi, '--bundle', $badBundle,
            '--output', $failedOutput) 'mask.*(grayscale|alpha 255|opaque|equal RGB)'
        Assert-FileUnchanged $failedOutput $marker "$kind failed build"
        Assert-S4mtFails @('image', 'import', '--package', $sourcePackage,
            '--tgi', $tgi, '--bundle', $badBundle, '--output', $failedOutput) `
            'mask.*(grayscale|alpha 255|opaque|equal RGB)'
        Assert-FileUnchanged $failedOutput $marker "$kind failed import"
        Assert-FileUnchanged $sourcePackage $sourcePackageBefore "$kind failed import source"
        $negativeCases.Add($kind)
    }

    $lowerMipBundle = Join-Path $tempRoot 'lower-mip.bundle'
    $null = Invoke-S4mtJson @('image', 'export', '--package', $builtPackage,
        '--tgi', $tgi, '--bundle', $lowerMipBundle, '--mip', '1')
    $lowerMipOutput = Join-Path $tempRoot 'lower-mip-failed.package'
    [byte[]] $marker = [byte[]]@(11, 22, 33, 44)
    [System.IO.File]::WriteAllBytes($lowerMipOutput, $marker)
    Assert-S4mtFails @('image', 'build', '--tgi', $tgi, '--bundle', $lowerMipBundle,
        '--output', $lowerMipOutput) 'mip 0'
    Assert-FileUnchanged $lowerMipOutput $marker 'nonzero mip failed build'
    Assert-S4mtFails @('image', 'import', '--package', $sourcePackage,
        '--tgi', $tgi, '--bundle', $lowerMipBundle, '--output', $lowerMipOutput) 'mip 0'
    Assert-FileUnchanged $lowerMipOutput $marker 'nonzero mip failed import'
    $negativeCases.Add('nonzero-mip')

    Assert-S4mtFails @('image', 'build', '--tgi', $tgi, '--bundle', $baseBundle,
        '--output', (Join-Path $baseBundle 'unsafe.package')) 'bundle|inside'
    if (Test-Path -LiteralPath (Join-Path $baseBundle 'unsafe.package')) {
        throw 'Unsafe package output was written inside the input bundle.'
    }
    Assert-S4mtFails @('image', 'import', '--package', $sourcePackage, '--tgi', $tgi,
        '--bundle', $baseBundle, '--output', $sourcePackage) 'source package|overwrite'
    Assert-FileUnchanged $sourcePackage $sourcePackageBefore 'overlapping import source'
    Assert-S4mtFails @('image', 'export', '--package', $builtPackage, '--tgi', $tgi,
        '--bundle', $baseBundle) 'already exists'
    $negativeCases.Add('path-overlap-and-existing-export')

    $result = [pscustomobject]@{
        result = 'verified'
        sourceFixture = [pscustomobject]@{ tgi = $tgi; sha256 = $fixture.sha256 }
        ffmpegVersion = $ffmpegVersion
        width = $width
        height = $height
        mipCount = $expectedMipCount
        buildReadBackVerified = $build.readBackVerified
        buildPixelVerifiedMipCount = $build.pixelVerifiedMipCount
        importReadBackVerified = $import.readBackVerified
        importPixelVerifiedMipCount = $import.pixelVerifiedMipCount
        generatedMipOracles = @($mipEvidence)
        generatedOpcodeBlocks = @($opcodeTotals)
        maskExactAcrossMips = $true
        importedPayloadEqualsBuild = $true
        unrelatedPayloadPreserved = $true
        negativeCases = @($negativeCases)
    }
    $auditPath = Join-Path $repoRoot '.audit\rles-workflow\writer-verification.json'
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $auditPath -Encoding utf8
    $result | ConvertTo-Json -Depth 8
}
finally {
    if (Test-Path -LiteralPath $tempRoot -PathType Container) {
        $rootFullPath = [System.IO.Path]::GetFullPath($tempRoot)
        $tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if (-not $rootFullPath.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetFileName($rootFullPath) -notlike 's4mt-rles-writer-*') {
            throw "Refusing cleanup outside the expected temporary directory: $rootFullPath"
        }
        Remove-Item -LiteralPath $rootFullPath -Recurse -Force
    }
}
