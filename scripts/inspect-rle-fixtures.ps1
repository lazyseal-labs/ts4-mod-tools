param(
    [string]$GameRoot = 'C:\Program Files\EA Games\The Sims 4',
    [string]$IndexPath = (Join-Path $env:LOCALAPPDATA 'S4MT\cache\game-index.jsonl.gz'),
    [int]$SamplesPerType = 3,
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\.audit\rle2-rles-codec-grounding\fixture-census.json')
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\S4MT.Cli'
$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
$resolvedIndexPath = [System.IO.Path]::GetFullPath($IndexPath)
$resolvedGameRoot = [System.IO.Path]::GetFullPath($GameRoot)

if (-not (Test-Path -LiteralPath $resolvedIndexPath -PathType Leaf)) {
    throw "Game index does not exist: $resolvedIndexPath"
}
if (-not (Test-Path -LiteralPath $resolvedGameRoot -PathType Container)) {
    throw "Game root does not exist: $resolvedGameRoot"
}
if ($SamplesPerType -lt 1 -or $SamplesPerType -gt 20) {
    throw 'SamplesPerType must be between 1 and 20.'
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "s4mt-rle-fixtures-$([guid]::NewGuid().ToString('N'))"
[System.IO.Directory]::CreateDirectory($tempRoot) | Out-Null
$fixtureTypes = @(
    [pscustomobject]@{ Name = 'RLE2'; Type = '0x3453CF95' },
    [pscustomobject]@{ Name = 'RLES'; Type = '0xBA856C78' }
)
$records = [System.Collections.Generic.List[object]]::new()

function Invoke-S4mtJson {
    param([string[]]$Arguments)

    $lines = & dotnet run --no-build --project $projectPath -- @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "S4MT command failed with exit ${LASTEXITCODE}: $($lines -join [Environment]::NewLine)"
    }
    return (($lines -join [Environment]::NewLine) | ConvertFrom-Json)
}

try {
    foreach ($fixtureType in $fixtureTypes) {
        $find = Invoke-S4mtJson @(
            'game', 'find', '--index', $resolvedIndexPath,
            '--type', $fixtureType.Type, '--limit', '1000'
        )
        $sampleNumber = 0
        $candidateNumber = 0
        $skippedDeletedProviders = 0
        $seenTgis = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($resource in $find.resources) {
            if ($sampleNumber -ge $SamplesPerType) {
                break
            }
            if ($seenTgis.Contains([string]$resource.tgi)) {
                continue
            }
            $candidateNumber++
            $payloadPath = Join-Path $tempRoot "$($fixtureType.Name.ToLowerInvariant())-$candidateNumber.bin"
            try {
                $packagePath = [System.IO.Path]::GetFullPath((Join-Path $resolvedGameRoot $resource.source))
                try {
                    $null = Invoke-S4mtJson @(
                        'resource', 'export', '--package', $packagePath,
                        '--tgi', $resource.tgi, '--output', $payloadPath
                    )
                    [void]$seenTgis.Add([string]$resource.tgi)
                }
                catch {
                    if ($_.Exception.Message -match 'Resource is marked as deleted') {
                        $skippedDeletedProviders++
                        continue
                    }
                    throw
                }
                $payload = [System.IO.File]::ReadAllBytes($payloadPath)
                $pixelFormat = if ($payload.Length -ge 4) { [Text.Encoding]::ASCII.GetString($payload, 0, 4) } else { '' }
                $encoding = if ($payload.Length -ge 8) { [Text.Encoding]::ASCII.GetString($payload, 4, 4) } else { '' }
                $record = [ordered]@{
                    format = $fixtureType.Name
                    tgi = $resource.tgi
                    package = $resource.source
                    packagePath = $packagePath
                    layer = $resource.layer
                    payloadBytes = $payload.Length
                    sha256 = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
                    pixelFormat = $pixelFormat
                    encoding = $encoding
                }

                if ($payload.Length -ge 16) {
                    $width = [BitConverter]::ToUInt16($payload, 8)
                    $height = [BitConverter]::ToUInt16($payload, 10)
                    $mipCount = [BitConverter]::ToUInt16($payload, 12)
                    $reserved = [BitConverter]::ToUInt16($payload, 14)
                    $offsetCount = if ($pixelFormat -eq 'L8') { 2 } elseif ($fixtureType.Name -eq 'RLE2') { 5 } else { 6 }
                    $tableEnd = 16L + [long]$mipCount * $offsetCount * 4L
                    $offsetRows = [System.Collections.Generic.List[object]]::new()
                    if ($mipCount -le 32 -and $tableEnd -le $payload.Length) {
                        for ($mipIndex = 0; $mipIndex -lt $mipCount; $mipIndex++) {
                            $offsets = [long[]]::new($offsetCount)
                            for ($streamIndex = 0; $streamIndex -lt $offsetCount; $streamIndex++) {
                                $offsetPosition = 16 + ($mipIndex * $offsetCount * 4) + ($streamIndex * 4)
                                $offsets[$streamIndex] = [BitConverter]::ToUInt32($payload, $offsetPosition)
                            }
                            $offsetRows.Add([pscustomobject]@{ mip = $mipIndex; offsets = $offsets })
                        }
                    }
                    $streamRanges = [System.Collections.Generic.List[object]]::new()
                    $allStreamMipOffsetsMonotonic = $true
                    if ($offsetRows.Count -eq $mipCount) {
                        for ($streamIndex = 0; $streamIndex -lt $offsetCount; $streamIndex++) {
                            $streamStart = $offsetRows[0].offsets[$streamIndex]
                            $streamEnd = if ($streamIndex + 1 -lt $offsetCount) {
                                $offsetRows[0].offsets[$streamIndex + 1]
                            }
                            else {
                                $payload.Length
                            }
                            $streamMipOffsets = @($offsetRows | ForEach-Object { $_.offsets[$streamIndex] })
                            $monotonic = $true
                            for ($mipIndex = 0; $mipIndex -lt $streamMipOffsets.Count; $mipIndex++) {
                                if ($streamMipOffsets[$mipIndex] -lt $streamStart -or
                                    $streamMipOffsets[$mipIndex] -ge $streamEnd -or
                                    ($mipIndex -gt 0 -and $streamMipOffsets[$mipIndex] -le $streamMipOffsets[$mipIndex - 1])) {
                                    $monotonic = $false
                                    $allStreamMipOffsetsMonotonic = $false
                                }
                            }
                            $streamRanges.Add([pscustomobject]@{
                                stream = $streamIndex
                                start = $streamStart
                                end = $streamEnd
                                byteCount = $streamEnd - $streamStart
                                monotonicMipOffsets = $monotonic
                            })
                        }
                    }

                    $mipCommandStats = [System.Collections.Generic.List[object]]::new()
                    $blocksByOpcode = [long[]]::new(4)
                    $commandCountsByOpcode = [long[]]::new(4)
                    if ($offsetRows.Count -eq $mipCount -and $mipCount -gt 0) {
                        $mipWidth = [long]$width
                        $mipHeight = [long]$height
                        for ($mipIndex = 0; $mipIndex -lt $mipCount; $mipIndex++) {
                            $commandStart = $offsetRows[$mipIndex].offsets[0]
                            $commandEnd = if ($mipIndex + 1 -lt $mipCount) {
                                $offsetRows[$mipIndex + 1].offsets[0]
                            }
                            else {
                                $offsetRows[0].offsets[1]
                            }
                            $opcodeCounts = [long[]]::new(4)
                            $mipBlocksByOpcode = [long[]]::new(4)
                            $decodedBlocks = 0L
                            $commandBytes = $commandEnd - $commandStart
                            if ($commandBytes -ge 0 -and ($commandBytes % 2) -eq 0) {
                                for ($position = $commandStart; $position -lt $commandEnd; $position += 2) {
                                    $command = [BitConverter]::ToUInt16($payload, [int]$position)
                                    $opcode = $command -band 3
                                    $count = $command -shr 2
                                    $opcodeCounts[$opcode]++
                                    $mipBlocksByOpcode[$opcode] += $count
                                    $blocksByOpcode[$opcode] += $count
                                    $commandCountsByOpcode[$opcode]++
                                    $decodedBlocks += $count
                                }
                            }
                            $expectedBlocks = [Math]::Ceiling($mipWidth / 4) * [Math]::Ceiling($mipHeight / 4)
                            $mipCommandStats.Add([pscustomobject]@{
                                mip = $mipIndex
                                width = $mipWidth
                                height = $mipHeight
                                expectedBlocks = $expectedBlocks
                                decodedBlocksFromUpper14Bits = $decodedBlocks
                                commandCountsByOpcode = @($opcodeCounts)
                                blockCountsByOpcode = @($mipBlocksByOpcode)
                                commandBytes = $commandBytes
                                commandBytesEven = ($commandBytes % 2) -eq 0
                            })
                            $mipWidth = [Math]::Max(1, [Math]::Floor($mipWidth / 2))
                            $mipHeight = [Math]::Max(1, [Math]::Floor($mipHeight / 2))
                        }
                    }

                    $expectedStreamBytes = [System.Collections.Generic.List[long]]::new()
                    $expectedStreamBytes.Add([long](($mipCommandStats | Measure-Object -Property commandBytes -Sum).Sum))
                    $texturedBlocks = $blocksByOpcode[1] + $blocksByOpcode[2]
                    if ($pixelFormat -eq 'DXT5' -and $encoding -eq 'RLE2') {
                        $expectedStreamBytes.Add($texturedBlocks * 4)
                        $expectedStreamBytes.Add($texturedBlocks * 4)
                        $expectedStreamBytes.Add($blocksByOpcode[1] * 2)
                        $expectedStreamBytes.Add($blocksByOpcode[1] * 6)
                    }
                    elseif ($pixelFormat -eq 'DXT5' -and $encoding -eq 'RLES') {
                        $expectedStreamBytes.Add($texturedBlocks * 4)
                        $expectedStreamBytes.Add($texturedBlocks * 4)
                        $expectedStreamBytes.Add($texturedBlocks * 2)
                        $expectedStreamBytes.Add($texturedBlocks * 6)
                        $expectedStreamBytes.Add($blocksByOpcode[1] * 16)
                    }
                    $actualStreamBytes = @($streamRanges | ForEach-Object { [long]$_.byteCount })
                    $streamLengthsMatchCommands = $expectedStreamBytes.Count -eq $actualStreamBytes.Count
                    if ($streamLengthsMatchCommands) {
                        for ($streamIndex = 0; $streamIndex -lt $expectedStreamBytes.Count; $streamIndex++) {
                            if ($expectedStreamBytes[$streamIndex] -ne $actualStreamBytes[$streamIndex]) {
                                $streamLengthsMatchCommands = $false
                            }
                        }
                    }
                    $allMipBlockCountsMatch = @($mipCommandStats | Where-Object {
                        $_.expectedBlocks -ne $_.decodedBlocksFromUpper14Bits -or -not $_.commandBytesEven
                    }).Count -eq 0

                    $record.widthCandidate = $width
                    $record.heightCandidate = $height
                    $record.mipCountCandidate = $mipCount
                    $record.reservedCandidate = $reserved
                    $record.offsetCountPerMipCandidate = $offsetCount
                    $record.tableEnd = $tableEnd
                    $record.streamRangesCandidate = @($streamRanges)
                    $record.allStreamMipOffsetsMonotonic = $allStreamMipOffsetsMonotonic
                    $record.mipCommandStatsCandidate = @($mipCommandStats)
                    $record.commandCountsByOpcode = @($commandCountsByOpcode)
                    $record.blocksByOpcode = @($blocksByOpcode)
                    $record.allMipBlockCountsMatch = $allMipBlockCountsMatch
                    $record.streamLengthsMatchCommands = $streamLengthsMatchCommands
                    $record.offsetTableFits = $tableEnd -le $payload.Length
                }

                $records.Add([pscustomobject]$record)
                $sampleNumber++
            }
            finally {
                if (Test-Path -LiteralPath $payloadPath -PathType Leaf) {
                    [System.IO.File]::Delete($payloadPath)
                }
            }
        }
        if ($sampleNumber -lt $SamplesPerType) {
            throw "Found only $sampleNumber readable $($fixtureType.Name) resources after examining $candidateNumber unique TGIs and skipping $skippedDeletedProviders deleted providers."
        }
        $fixtureType | Add-Member -NotePropertyName SkippedDeletedProviders -NotePropertyValue $skippedDeletedProviders
    }

    $result = [ordered]@{
        schemaVersion = 1
        gameRoot = $resolvedGameRoot
        index = $resolvedIndexPath
        sampledAtUtc = [DateTime]::UtcNow.ToString('O')
        sampleCountPerType = $SamplesPerType
        skippedDeletedProviders = @($fixtureTypes | ForEach-Object {
            [pscustomobject]@{ format = $_.Name; count = $_.SkippedDeletedProviders }
        })
        records = @($records)
    }
    $json = $result | ConvertTo-Json -Depth 12
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($resolvedOutputPath)) | Out-Null
    [System.IO.File]::WriteAllText($resolvedOutputPath, $json, [System.Text.UTF8Encoding]::new($false))
    $summary = @($records | ForEach-Object {
        [pscustomobject]@{
            format = $_.format
            tgi = $_.tgi
            pixelFormat = $_.pixelFormat
            encoding = $_.encoding
            width = $_.widthCandidate
            height = $_.heightCandidate
            mips = $_.mipCountCandidate
            payloadBytes = $_.payloadBytes
            streamBytes = @($_.streamRangesCandidate | ForEach-Object byteCount)
            offsetColumnsMonotonic = $_.allStreamMipOffsetsMonotonic
            mipBlockCountsMatch = $_.allMipBlockCountsMatch
            streamLengthsMatchCommands = $_.streamLengthsMatchCommands
        }
    })
    Write-Output ($summary | ConvertTo-Json -Depth 5)
}
finally {
    if (Test-Path -LiteralPath $tempRoot -PathType Container) {
        $rootFullPath = [System.IO.Path]::GetFullPath($tempRoot)
        $tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if (-not $rootFullPath.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetFileName($rootFullPath) -notlike 's4mt-rle-fixtures-*') {
            throw "Refusing cleanup outside the expected temporary directory: $rootFullPath"
        }
        if ([System.IO.Directory]::GetFileSystemEntries($rootFullPath).Count -eq 0) {
            [System.IO.Directory]::Delete($rootFullPath, $false)
        }
    }
}
