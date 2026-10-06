param(
    [string] $GameRoot = 'C:\Program Files\EA Games\The Sims 4',
    [string] $FfmpegPath = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cliProject = Join-Path $repoRoot 'src\S4MT.Cli'
$fixtureManifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'rle-dxt5-fixtures.json') -Raw | ConvertFrom-Json
$fixturePackage = [System.IO.Path]::GetFullPath((Join-Path ([System.IO.Path]::GetFullPath($GameRoot)) $fixtureManifest.package))

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
if (-not (Test-Path -LiteralPath $fixturePackage -PathType Leaf)) {
    throw "Pinned RLES fixture package does not exist: $fixturePackage"
}
if (-not [BitConverter]::IsLittleEndian) {
    throw 'The RLES fixture oracle requires a little-endian host.'
}
$ffmpegVersionOutput = & $FfmpegPath -version 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "FFmpeg failed to report its version: $($ffmpegVersionOutput -join [Environment]::NewLine)"
}
$ffmpegVersion = [string]($ffmpegVersionOutput | Select-Object -First 1)
$ffmpegDecoders = & $FfmpegPath -hide_banner -decoders 2>&1
if ($LASTEXITCODE -ne 0 -or (($ffmpegDecoders | Out-String) -notmatch '\bdds\s+DirectDraw Surface image decoder')) {
    throw 'The selected FFmpeg build must include the DDS image decoder.'
}

function Invoke-S4mtJson {
    param([string[]] $Arguments)
    $lines = & dotnet run --no-build --project $cliProject -- @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "S4MT exited $LASTEXITCODE`: $($lines -join [Environment]::NewLine)"
    }
    return (($lines | Out-String) | ConvertFrom-Json)
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

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('s4mt-rles-oracle-' + [guid]::NewGuid().ToString('N'))
try {
    $fixtures = @($fixtureManifest.fixtures | Where-Object type -eq 'rles' | Select-Object -First 3)
    if ($fixtures.Count -ne 3) { throw 'Expected three pinned RLES fixtures.' }
    New-Item -ItemType Directory -Path $tempRoot | Out-Null

    [long[]] $opcodeTotals = [long[]]::new(3)
    $verifiedMipCount = 0
    $verifiedTgis = [System.Collections.Generic.List[string]]::new()
    for ($fixtureIndex = 0; $fixtureIndex -lt $fixtures.Count; $fixtureIndex++) {
        $fixture = $fixtures[$fixtureIndex]
        $payloadPath = Join-Path $tempRoot "fixture-$fixtureIndex.bin"
        $null = Invoke-S4mtJson @('resource', 'export', '--package', $fixturePackage, '--tgi', $fixture.tgi, '--output', $payloadPath)
        $payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($payloadHash -ne $fixture.sha256) {
            throw "Extracted RLES fixture does not match pinned SHA-256: $($fixture.tgi)"
        }
        [byte[]] $payload = [System.IO.File]::ReadAllBytes($payloadPath)
        $mipCount = [int][BitConverter]::ToUInt16($payload, 12)
        if ($mipCount -ne 11) { throw "Pinned RLES fixture has unexpected mip count: $($fixture.tgi)" }
        $mipsToVerify = if ($fixtureIndex -eq 0) { 0..10 } else { @(0) }

        foreach ($mip in $mipsToVerify) {
            $name = "fixture-$fixtureIndex-mip-$mip"
            $bundlePath = Join-Path $tempRoot "$name.bundle"
            $ddsPath = Join-Path $tempRoot "$name.oracle.dds"
            $actualColorRawPath = Join-Path $tempRoot "$name.color.rgba"
            $oracleColorRawPath = Join-Path $tempRoot "$name.oracle.rgba"
            $actualMaskRawPath = Join-Path $tempRoot "$name.mask.rgba"
            $oracle = Get-RlesMipOracle $payload $mip $ddsPath
            $report = Invoke-S4mtJson @('image', 'export', '--package', $fixturePackage, '--tgi', $fixture.tgi,
                '--bundle', $bundlePath, '--mip', [string]$mip)
            $manifestPath = Join-Path $bundlePath 'manifest.json'
            $colorPath = Join-Path $bundlePath 'color.png'
            $maskPath = Join-Path $bundlePath 'mask.png'
            if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
                -not (Test-Path -LiteralPath $colorPath -PathType Leaf) -or
                -not (Test-Path -LiteralPath $maskPath -PathType Leaf)) {
                throw "RLES export did not publish a complete bundle at $($fixture.tgi), mip $mip."
            }
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            if ($manifest.schemaVersion -ne 1 -or $manifest.codec -ne 'RLES' -or
                (Normalize-Tgi ([string]$manifest.sourceTgi)) -ne (Normalize-Tgi ([string]$fixture.tgi)) -or
                $manifest.sourceWidth -ne $oracle.baseWidth -or $manifest.sourceHeight -ne $oracle.baseHeight -or
                $manifest.width -ne $oracle.width -or $manifest.height -ne $oracle.height -or
                $manifest.mipLevel -ne $mip -or $manifest.mipCount -ne $oracle.mipCount -or
                $manifest.colorFile -ne 'color.png' -or $manifest.colorFormat -ne 'rgba8' -or
                $manifest.maskFile -ne 'mask.png' -or $manifest.maskFormat -ne 'grayscale8-opaque') {
                throw "RLES bundle manifest metadata differs at $($fixture.tgi), mip $mip."
            }
            if ($report.codec -ne 'rles' -or $report.width -ne $oracle.width -or
                $report.height -ne $oracle.height -or $report.mipLevel -ne $mip -or
                $report.mipCount -ne $oracle.mipCount) {
                throw "RLES export report metadata differs at $($fixture.tgi), mip $mip."
            }

            Convert-ToRawRgba $colorPath $actualColorRawPath
            Convert-ToRawRgba $ddsPath $oracleColorRawPath
            Convert-ToRawRgba $maskPath $actualMaskRawPath
            [byte[]] $actualColor = [System.IO.File]::ReadAllBytes($actualColorRawPath)
            [byte[]] $oracleColor = [System.IO.File]::ReadAllBytes($oracleColorRawPath)
            [byte[]] $actualMask = [System.IO.File]::ReadAllBytes($actualMaskRawPath)
            Assert-EqualBytes $actualColor $oracleColor "RLES color $($fixture.tgi), mip $mip"
            Assert-EqualBytes $actualMask $oracle.maskRgba "RLES mask $($fixture.tgi), mip $mip"
            for ($opcode = 0; $opcode -lt 3; $opcode++) {
                $opcodeTotals[$opcode] += $oracle.opcodeTotals[$opcode]
            }
            $verifiedMipCount++
        }
        $verifiedTgis.Add([string]$fixture.tgi)
    }
    if ($opcodeTotals[0] -eq 0 -or $opcodeTotals[1] -eq 0 -or $opcodeTotals[2] -eq 0) {
        throw "Selected RLES fixtures did not cover opcodes 0, 1, and 2: [$($opcodeTotals -join ',')]."
    }
    [pscustomobject]@{
        verifiedFixtures = $verifiedTgis.Count
        verifiedMipExports = $verifiedMipCount
        verifiedTgis = @($verifiedTgis)
        opcodeBlocks = @($opcodeTotals)
        ffmpegVersion = $ffmpegVersion
        colorPixelOracleMatched = $true
        maskPixelOracleMatched = $true
        manifestMetadataMatched = $true
    } | ConvertTo-Json -Compress
}
finally {
    if (Test-Path -LiteralPath $tempRoot -PathType Container) {
        $rootFullPath = [System.IO.Path]::GetFullPath($tempRoot)
        $tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if (-not $rootFullPath.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetFileName($rootFullPath) -notlike 's4mt-rles-oracle-*') {
            throw "Refusing cleanup outside the expected temporary directory: $rootFullPath"
        }
        Remove-Item -LiteralPath $rootFullPath -Recurse -Force
    }
}
