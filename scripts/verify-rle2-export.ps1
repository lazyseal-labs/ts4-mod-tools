param(
    [string] $GameRoot = 'C:\Program Files\EA Games\The Sims 4',
    [string] $FfmpegPath = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cliProject = Join-Path $repoRoot 'src\S4MT.Cli'
$fixtureManifestPath = Join-Path $PSScriptRoot 'rle-dxt5-fixtures.json'
$fixtureManifest = Get-Content -LiteralPath $fixtureManifestPath -Raw | ConvertFrom-Json
$resolvedGameRoot = [System.IO.Path]::GetFullPath($GameRoot)
$fixturePackage = [System.IO.Path]::GetFullPath((Join-Path $resolvedGameRoot $fixtureManifest.package))

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
$ffmpegDecoders = & $FfmpegPath -hide_banner -decoders 2>&1
if ($LASTEXITCODE -ne 0 -or (($ffmpegDecoders | Out-String) -notmatch '\bdds\s+DirectDraw Surface image decoder')) {
    throw 'The selected FFmpeg build must include the DDS image decoder.'
}
if (-not [BitConverter]::IsLittleEndian) {
    throw 'The independent RLE2 fixture reader requires a little-endian host.'
}

function Invoke-S4mtJson {
    param([string[]] $Arguments, [int] $ExpectedExitCode = 0)
    $lines = & dotnet run --no-build --project $cliProject -- @Arguments 2>&1
    $exit = $LASTEXITCODE
    if ($exit -ne $ExpectedExitCode) {
        throw "S4MT exited $exit instead of $ExpectedExitCode`: $($lines -join [Environment]::NewLine)"
    }
    return (($lines | Out-String) | ConvertFrom-Json)
}

function Assert-S4mtFailure {
    param([string[]] $Arguments, [int] $ExpectedExitCode, [string] $ExpectedText)
    $lines = & dotnet run --no-build --project $cliProject -- @Arguments 2>&1
    $exit = $LASTEXITCODE
    $output = $lines | Out-String
    if ($exit -ne $ExpectedExitCode -or $output -notlike "*$ExpectedText*") {
        throw "S4MT failed unexpectedly (exit $exit): $output"
    }
}

function Copy-PayloadBytes {
    param([byte[]] $Source, [int] $SourceOffset, [byte[]] $Destination, [int] $DestinationOffset, [int] $Count)
    if ($SourceOffset -lt 0 -or $Count -lt 0 -or $SourceOffset -gt $Source.Length - $Count -or
        $DestinationOffset -lt 0 -or $DestinationOffset -gt $Destination.Length - $Count) {
        throw "The independent RLE2 oracle encountered an invalid range: source=$SourceOffset+$Count/$($Source.Length), destination=$DestinationOffset+$Count/$($Destination.Length)."
    }
    [Array]::Copy($Source, $SourceOffset, $Destination, $DestinationOffset, $Count)
}

function Set-DdsUInt32 {
    param([byte[]] $Header, [int] $Offset, [uint32] $Value)
    [Array]::Copy([BitConverter]::GetBytes($Value), 0, $Header, $Offset, 4)
}

function New-Rle2MipDds {
    param([byte[]] $Payload, [int] $MipLevel, [string] $OutputPath)

    if ($Payload.Length -lt 16 -or [Text.Encoding]::ASCII.GetString($Payload, 0, 4) -ne 'DXT5' -or
        [Text.Encoding]::ASCII.GetString($Payload, 4, 4) -ne 'RLE2') {
        throw 'The independent oracle expected a DXT5 RLE2 payload.'
    }

    $baseWidth = [BitConverter]::ToUInt16($Payload, 8)
    $baseHeight = [BitConverter]::ToUInt16($Payload, 10)
    $mipCount = [BitConverter]::ToUInt16($Payload, 12)
    $tableEnd = 16 + $mipCount * 5 * 4
    if ($MipLevel -lt 0 -or $MipLevel -ge $mipCount -or $Payload.Length -lt $tableEnd) {
        throw "Mip $MipLevel is outside the oracle payload's $mipCount mips or its table is truncated."
    }

    [int[]] $offsets = [int[]]::new($mipCount * 5)
    for ($mip = 0; $mip -lt $mipCount; $mip++) {
        for ($stream = 0; $stream -lt 5; $stream++) {
            $offsets[$mip * 5 + $stream] = [int][BitConverter]::ToUInt32($Payload, 16 + ($mip * 5 + $stream) * 4)
        }
    }

    [int[]] $streamStarts = [int[]]::new(5)
    [int[]] $streamEnds = [int[]]::new(5)
    for ($stream = 0; $stream -lt 5; $stream++) {
        $streamStarts[$stream] = $offsets[$stream]
        $streamEnds[$stream] = if ($stream -lt 4) { $offsets[$stream + 1] } else { $Payload.Length }
        if ($streamStarts[$stream] -lt $tableEnd -or $streamStarts[$stream] -gt $streamEnds[$stream] -or
            $streamEnds[$stream] -gt $Payload.Length) {
            throw "The independent oracle found an invalid overall stream $stream range."
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
    [int[]] $cursors = [int[]]::new(5)
    [int[]] $ends = [int[]]::new(5)
    for ($stream = 0; $stream -lt 5; $stream++) {
        $cursors[$stream] = $offsets[$MipLevel * 5 + $stream]
        $ends[$stream] = if ($MipLevel + 1 -lt $mipCount) {
            $offsets[($MipLevel + 1) * 5 + $stream]
        } else {
            $streamEnds[$stream]
        }
        if ($cursors[$stream] -lt $streamStarts[$stream] -or $cursors[$stream] -gt $ends[$stream] -or
            $ends[$stream] -gt $streamEnds[$stream]) {
            throw "The independent oracle found an invalid mip $MipLevel stream $stream range."
        }
    }
    $transparentBlock = [byte[]]@(0, 5, 0, 0, 0, 0, 0, 0, 255, 255, 0, 0, 0, 0, 0, 0)
    $opaqueAlpha = [byte[]]@(0, 5, 255, 255, 255, 255, 255, 255)
    [long[]] $opcodeTotals = [long[]]::new(3)
    $probeCursor = $cursors[0]
    while ($probeCursor -lt $ends[0]) {
        $probeCommand = [BitConverter]::ToUInt16($Payload, $probeCursor)
        $probeCursor += 2
        $probeOpcode = $probeCommand -band 3
        if ($probeOpcode -eq 3 -or ($probeCommand -shr 2) -eq 0) {
            throw 'The independent oracle encountered an unsupported or zero-count RLE2 command.'
        }
        $opcodeTotals[$probeOpcode] += ($probeCommand -shr 2)
    }
    if (($opcodeTotals[0] + $opcodeTotals[1] + $opcodeTotals[2]) -ne $expectedBlocks -or
        ($opcodeTotals[1] * 2) -ne ($ends[3] - $cursors[3]) -or
        ($opcodeTotals[1] * 6) -ne ($ends[4] - $cursors[4]) -or
        (($opcodeTotals[1] + $opcodeTotals[2]) * 4) -ne ($ends[1] - $cursors[1]) -or
        (($opcodeTotals[1] + $opcodeTotals[2]) * 4) -ne ($ends[2] - $cursors[2])) {
        throw 'The independent oracle command totals do not match the mip stream lengths.'
    }

    $blockCursor = $cursors[0]
    $blockIndex = 0
    while ($blockCursor -lt $ends[0]) {
        if ($ends[0] - $blockCursor -lt 2) {
            throw 'The independent oracle encountered a truncated RLE2 command.'
        }
        $command = [BitConverter]::ToUInt16($Payload, $blockCursor)
        $blockCursor += 2
        $opcode = $command -band 3
        $count = $command -shr 2
        if ($opcode -eq 3 -or $count -eq 0 -or $blockIndex + $count -gt $expectedBlocks) {
            throw 'The independent oracle encountered an unsupported or out-of-range RLE2 command.'
        }

        for ($run = 0; $run -lt $count; $run++) {
            $destination = $blockIndex * 16
            if ($opcode -eq 0) {
                [Array]::Copy($transparentBlock, 0, $blocks, $destination, 16)
            }
            elseif ($opcode -eq 1) {
                Copy-PayloadBytes $Payload $cursors[3] $blocks $destination 2
                Copy-PayloadBytes $Payload $cursors[4] $blocks ($destination + 2) 6
                Copy-PayloadBytes $Payload $cursors[1] $blocks ($destination + 8) 4
                Copy-PayloadBytes $Payload $cursors[2] $blocks ($destination + 12) 4
                $cursors[3] += 2
                $cursors[4] += 6
                $cursors[1] += 4
                $cursors[2] += 4
            }
            else {
                [Array]::Copy($opaqueAlpha, 0, $blocks, $destination, 8)
                Copy-PayloadBytes $Payload $cursors[1] $blocks ($destination + 8) 4
                Copy-PayloadBytes $Payload $cursors[2] $blocks ($destination + 12) 4
                $cursors[1] += 4
                $cursors[2] += 4
            }
            $blockIndex++
        }
    }

    if ($blockIndex -ne $expectedBlocks -or $blockCursor -ne $ends[0]) {
        throw "The independent oracle decoded $blockIndex blocks; expected $expectedBlocks."
    }
    for ($stream = 1; $stream -lt 5; $stream++) {
        if ($cursors[$stream] -ne $ends[$stream]) {
            throw "The independent oracle consumed $($cursors[$stream] - $offsets[$MipLevel * 5 + $stream]) bytes from stream $stream; its mip length differs."
        }
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

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('s4mt-rle2-oracle-' + [guid]::NewGuid().ToString('N'))
try {
    $fixtures = @($fixtureManifest.fixtures | Where-Object type -eq 'rle2' | Select-Object -First 3)
    if ($fixtures.Count -ne 3 -or -not (Test-Path -LiteralPath $fixturePackage -PathType Leaf)) {
        throw "Expected three pinned RLE2 fixtures and source package: $fixturePackage"
    }
    New-Item -ItemType Directory -Path $tempRoot | Out-Null

    $verifiedMipCount = 0
    $verifiedFixtures = [System.Collections.Generic.List[string]]::new()
    for ($fixtureIndex = 0; $fixtureIndex -lt $fixtures.Count; $fixtureIndex++) {
        $fixture = $fixtures[$fixtureIndex]
        $payloadPath = Join-Path $tempRoot "fixture-$fixtureIndex.bin"
        $payloadReport = Invoke-S4mtJson @('resource', 'export', '--package', $fixturePackage, '--tgi', $fixture.tgi, '--output', $payloadPath)
        $payloadHash = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($payloadHash -ne $fixture.sha256) {
            throw "Extracted RLE2 fixture does not match its pinned SHA-256: $($fixture.tgi)"
        }

        [byte[]] $payload = [System.IO.File]::ReadAllBytes($payloadPath)
        $mipCount = [BitConverter]::ToUInt16($payload, 12)
        $mipsToVerify = if ($fixtureIndex -eq 0) { 0..($mipCount - 1) } else { @(0) }
        foreach ($mip in $mipsToVerify) {
            $baseName = "fixture-$fixtureIndex-mip-$mip"
            $pngPath = Join-Path $tempRoot "$baseName.png"
            $ddsPath = Join-Path $tempRoot "$baseName.oracle.dds"
            $actualRawPath = Join-Path $tempRoot "$baseName.actual.rgba"
            $oracleRawPath = Join-Path $tempRoot "$baseName.oracle.rgba"

            $report = Invoke-S4mtJson @(
                'image', 'export', '--package', $fixturePackage, '--tgi', $fixture.tgi,
                '--output', $pngPath, '--mip', [string]$mip
            )
            $oracle = New-Rle2MipDds $payload $mip $ddsPath
            if ($report.codec -ne 'rle2' -or $report.mipLevel -ne $mip -or $report.mipCount -ne $oracle.mipCount -or
                $report.width -ne $oracle.width -or $report.height -ne $oracle.height) {
                throw "RLE2 export metadata differs at $($fixture.tgi), mip $mip."
            }

            Convert-ToRawRgba $pngPath $actualRawPath
            Convert-ToRawRgba $ddsPath $oracleRawPath
            $expectedByteCount = [long]$oracle.width * $oracle.height * 4
            $actualInfo = Get-Item -LiteralPath $actualRawPath
            $oracleInfo = Get-Item -LiteralPath $oracleRawPath
            if ($actualInfo.Length -ne $expectedByteCount -or $oracleInfo.Length -ne $expectedByteCount) {
                throw "Decoded RGBA byte count is wrong at $($fixture.tgi), mip $mip."
            }

            $actualHash = (Get-FileHash -LiteralPath $actualRawPath -Algorithm SHA256).Hash.ToLowerInvariant()
            $oracleHash = (Get-FileHash -LiteralPath $oracleRawPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actualHash -ne $oracleHash) {
                [byte[]] $actualBytes = [System.IO.File]::ReadAllBytes($actualRawPath)
                [byte[]] $oracleBytes = [System.IO.File]::ReadAllBytes($oracleRawPath)
                $firstDifference = -1
                $differenceCount = 0
                $maximumDelta = 0
                [long[]] $channelDifferences = [long[]]::new(4)
                $deltaCounts = @{}
                for ($index = 0; $index -lt $actualBytes.Length; $index++) {
                    if ($actualBytes[$index] -ne $oracleBytes[$index]) {
                        if ($firstDifference -lt 0) { $firstDifference = $index }
                        $differenceCount++
                        $delta = [Math]::Abs([int]$actualBytes[$index] - [int]$oracleBytes[$index])
                        $maximumDelta = [Math]::Max($maximumDelta, $delta)
                        $channelDifferences[$index % 4]++
                        $deltaKey = [string]$delta
                        if (-not $deltaCounts.ContainsKey($deltaKey)) { $deltaCounts[$deltaKey] = 0 }
                        $deltaCounts[$deltaKey]++
                    }
                }
                $pixelIndex = [int][Math]::Floor($firstDifference / 4)
                $pixelX = $pixelIndex % $oracle.width
                $pixelY = [int][Math]::Floor($pixelIndex / $oracle.width)
                $blockIndex = [int][Math]::Floor($pixelY / 4) * [int][Math]::Ceiling($oracle.width / 4.0) +
                    [int][Math]::Floor($pixelX / 4)
                $blockOffset = 128 + $blockIndex * 16
                $color0 = [BitConverter]::ToUInt16([System.IO.File]::ReadAllBytes($ddsPath), $blockOffset + 8)
                $color1 = [BitConverter]::ToUInt16([System.IO.File]::ReadAllBytes($ddsPath), $blockOffset + 10)
                throw "RLE2 pixels differ from FFmpeg at $($fixture.tgi), mip $mip; first pixel ($pixelX,$pixelY), channel $($firstDifference % 4), S4MT=$($actualBytes[$firstDifference]), FFmpeg=$($oracleBytes[$firstDifference]), differing bytes=$differenceCount, max delta=$maximumDelta, per-channel=[$($channelDifferences -join ',')], delta histogram=$($deltaCounts | ConvertTo-Json -Compress), BC3 color endpoints=[$color0,$color1]."
            }

            if ($fixtureIndex -eq 0 -and $mip -eq 0) {
                $defaultPngPath = Join-Path $tempRoot 'fixture-0-default-mip.png'
                $defaultRawPath = Join-Path $tempRoot 'fixture-0-default-mip.rgba'
                $defaultReport = Invoke-S4mtJson @(
                    'image', 'export', '--package', $fixturePackage, '--tgi', $fixture.tgi,
                    '--output', $defaultPngPath
                )
                if ($defaultReport.mipLevel -ne 0 -or $defaultReport.mipCount -ne $oracle.mipCount -or
                    $defaultReport.width -ne $oracle.width -or $defaultReport.height -ne $oracle.height) {
                    throw 'RLE2 export without --mip did not select mip level 0.'
                }
                Convert-ToRawRgba $defaultPngPath $defaultRawPath
                $defaultHash = (Get-FileHash -LiteralPath $defaultRawPath -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($defaultHash -ne $oracleHash) {
                    throw 'RLE2 export without --mip differs from the independent mip-0 pixel oracle.'
                }
            }
            $verifiedMipCount++
        }
        $verifiedFixtures.Add([string]$fixture.tgi)
    }

    $firstFixture = $fixtures[0]
    $firstMipCount = [BitConverter]::ToUInt16([System.IO.File]::ReadAllBytes((Join-Path $tempRoot 'fixture-0.bin')), 12)
    Assert-S4mtFailure @(
        'image', 'export', '--package', $fixturePackage, '--tgi', $firstFixture.tgi,
        '--output', (Join-Path $tempRoot 'negative-mip.png'), '--mip', '-1'
    ) 2 'non-negative integer'
    Assert-S4mtFailure @(
        'image', 'export', '--package', $fixturePackage, '--tgi', $firstFixture.tgi,
        '--output', (Join-Path $tempRoot 'out-of-range-mip.png'), '--mip', [string]$firstMipCount
    ) 2 'outside the available range'

    $rlesFixture = $fixtureManifest.fixtures | Where-Object type -eq 'rles' | Select-Object -First 1
    Assert-S4mtFailure @(
        'image', 'export', '--package', 'missing.package', '--tgi', $rlesFixture.tgi,
        '--output', (Join-Path $tempRoot 'rles.png')
    ) 2 'RLES image export requires'

    [pscustomobject]@{
        verifiedFixtures = $verifiedFixtures.Count
        verifiedMipExports = $verifiedMipCount
        verifiedTgis = @($verifiedFixtures)
        ffmpegPath = $FfmpegPath
        ffmpegVersion = $ffmpegVersion
        pixelOracleMatched = $true
        invalidMipInputsRejected = $true
        rlesSinglePngExportRejected = $true
    } | ConvertTo-Json -Compress
}
finally {
    if (Test-Path -LiteralPath $tempRoot -PathType Container) {
        $rootFullPath = [System.IO.Path]::GetFullPath($tempRoot)
        $tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if (-not $rootFullPath.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetFileName($rootFullPath) -notlike 's4mt-rle2-oracle-*') {
            throw "Refusing cleanup outside the expected temporary directory: $rootFullPath"
        }
        Remove-Item -LiteralPath $rootFullPath -Recurse -Force
    }
}
