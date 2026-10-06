$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cliProject = Join-Path $repoRoot 'src\S4MT.Cli'
$sourcePackage = 'C:\Program Files\EA Games\The Sims 4\Data\Client\ClientDeltaBuild6.package'
$v1Tgi = '0x2BC04EDF:0x00000000:0x800B7B75A476F5FE'
$v2Tgi = '0x2BC04EDF:0x00000000:0x802EB51D443F6176'
$v1Digest = 'b86a223b6c7c9584e76038252c46e60733ff18477122f72c550f44cb7a9a0c04'
$v2Digest = '61fdd07179ad24d180505de20042cbbc13065d035a975d46766944f47fc946ef'

if (-not (Test-Path -LiteralPath $sourcePackage -PathType Leaf)) {
    throw "Fixture package was not found: $sourcePackage"
}

function Invoke-S4mtJson {
    param([string[]] $Arguments, [int] $ExpectedExitCode = 0)
    $lines = & dotnet run --no-build --project $cliProject -- @Arguments 2>&1
    $exit = $LASTEXITCODE
    if ($exit -ne $ExpectedExitCode) {
        throw "S4MT exited $exit instead of $ExpectedExitCode`: $($lines -join [Environment]::NewLine)"
    }
    return ($lines | Out-String | ConvertFrom-Json)
}

function Write-JsonFile {
    param([string] $Path, [object] $Value)
    [System.IO.File]::WriteAllText(
        $Path,
        (ConvertTo-Json -InputObject $Value -Depth 8),
        [System.Text.UTF8Encoding]::new($false))
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('s4mt-lrle-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    $v1Raw = Join-Path $tempRoot 'v1.bin'
    $v2Raw = Join-Path $tempRoot 'v2.bin'
    $v1Png = Join-Path $tempRoot 'v1.png'
    $v2Png = Join-Path $tempRoot 'v2.png'
    $builtPackage = Join-Path $tempRoot 'built.package'
    $hiddenAlphaPng = Join-Path $tempRoot 'hidden-alpha.png'
    $hiddenAlphaPackage = Join-Path $tempRoot 'hidden-alpha.package'
    $basePackage = Join-Path $tempRoot 'base.package'
    $importedPackage = Join-Path $tempRoot 'imported.package'
    $sentinelPath = Join-Path $tempRoot 'sentinel.bin'
    $exportedSentinel = Join-Path $tempRoot 'sentinel-readback.bin'

    foreach ($fixture in @(
        @{ tgi = $v1Tgi; raw = $v1Raw; png = $v1Png; digest = $v1Digest },
        @{ tgi = $v2Tgi; raw = $v2Raw; png = $v2Png; digest = $v2Digest })) {
        $rawReport = Invoke-S4mtJson -Arguments @('resource', 'export', '--package', $sourcePackage,
            '--tgi', $fixture.tgi, '--output', $fixture.raw)
        $actualDigest = (Get-FileHash -LiteralPath $fixture.raw -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualDigest -ne $fixture.digest) {
            throw "LRLE fixture digest changed for $($fixture.tgi): $actualDigest"
        }
        $export = Invoke-S4mtJson -Arguments @('image', 'export', '--package', $sourcePackage,
            '--tgi', $fixture.tgi, '--output', $fixture.png)
        if ($export.codec -ne 'lrle' -or $export.width -ne 1024 -or $export.height -ne 2048 -or
            -not (Test-Path -LiteralPath $fixture.png -PathType Leaf)) {
            throw "LRLE fixture export did not decode: $($export | ConvertTo-Json -Compress)"
        }
    }

    $build = Invoke-S4mtJson -Arguments @('image', 'build', '--tgi', $v1Tgi,
        '--png', $v1Png, '--output', $builtPackage)
    if (-not $build.readBackVerified -or $build.identicalPixels -ne 2097152 -or
        $build.totalPixels -ne 2097152 -or $build.maximumChannelDelta -ne 0) {
        throw "LRLE build was not RGBA exact: $($build | ConvertTo-Json -Compress)"
    }

    # Every pixel is RGBA(123,45,67,0); alpha-zero RGB must survive v2 palette encoding.
    [System.IO.File]::WriteAllBytes($hiddenAlphaPng, [Convert]::FromBase64String(
        'iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAAEklEQVR4nGOo1nVmQMYMpAsAAP6LDrHO+JgsAAAAAElFTkSuQmCC'))
    $hiddenAlpha = Invoke-S4mtJson -Arguments @('image', 'build', '--tgi', $v1Tgi,
        '--png', $hiddenAlphaPng, '--output', $hiddenAlphaPackage)
    if (-not $hiddenAlpha.readBackVerified -or $hiddenAlpha.width -ne 4 -or
        $hiddenAlpha.height -ne 4 -or $hiddenAlpha.identicalPixels -ne 16 -or
        $hiddenAlpha.totalPixels -ne 16 -or $hiddenAlpha.maximumChannelDelta -ne 0) {
        throw "LRLE did not preserve RGB under zero alpha: $($hiddenAlpha | ConvertTo-Json -Compress)"
    }

    $builtRaw = Join-Path $tempRoot 'built-v2.bin'
    $null = Invoke-S4mtJson -Arguments @('resource', 'export', '--package', $builtPackage,
        '--tgi', $v1Tgi, '--output', $builtRaw)
    $builtBytes = [System.IO.File]::ReadAllBytes($builtRaw)
    if ($builtBytes.Length -lt 56 -or
        [System.Text.Encoding]::ASCII.GetString($builtBytes, 0, 4) -ne 'LRLE' -or
        [BitConverter]::ToUInt32($builtBytes, 4) -ne 0x32303056 -or
        [BitConverter]::ToUInt32($builtBytes, 12) -ne 9 -or
        [BitConverter]::ToUInt32($builtBytes, 16) -ne 0) {
        throw 'Generated LRLE v2 header or first mip offset is invalid.'
    }
    $paletteCount = [BitConverter]::ToUInt32($builtBytes, 52)
    $dataStart = 56 + [int]$paletteCount * 4
    if ($paletteCount -gt 65536 -or $dataStart -ge $builtBytes.Length) {
        throw 'Generated LRLE v2 palette exceeds its declared bounds.'
    }
    $previousOffset = -1
    for ($i = 0; $i -lt 9; $i++) {
        $offset = [BitConverter]::ToUInt32($builtBytes, 16 + 4 * $i)
        if ($offset -le $previousOffset -or $dataStart + $offset -ge $builtBytes.Length) {
            throw "Generated LRLE v2 mip $i has an invalid offset."
        }
        $previousOffset = $offset
    }

    [System.IO.File]::WriteAllBytes($sentinelPath, [byte[]](17, 34, 51, 68, 85))
    $manifestPath = Join-Path $tempRoot 'manifest.json'
    Write-JsonFile $manifestPath ([ordered]@{
        format = 1
        resources = @(
            [ordered]@{ type = '0x2BC04EDF'; group = '0x00000000'; instance = '0x800B7B75A476F5FE'; source = 'v1.bin'; encoding = 'bytes' },
            [ordered]@{ type = '0x220557DA'; group = '0x00000000'; instance = '0x0123456789ABCDEF'; source = 'sentinel.bin'; encoding = 'bytes' }
        )
    })
    $packageBuild = Invoke-S4mtJson -Arguments @('build', '--manifest', $manifestPath, '--output', $basePackage)
    if (-not $packageBuild.verified -or $packageBuild.resourceCount -ne 2) {
        throw 'Could not build the isolated import package.'
    }
    $import = Invoke-S4mtJson -Arguments @('image', 'import', '--package', $basePackage,
        '--tgi', $v1Tgi, '--png', $v2Png, '--output', $importedPackage)
    if (-not $import.readBackVerified -or $import.packageResourceCount -ne 2 -or
        $import.identicalPixels -ne $import.totalPixels -or $import.maximumChannelDelta -ne 0) {
        throw "LRLE import did not preserve pixels and keys: $($import | ConvertTo-Json -Compress)"
    }
    $null = Invoke-S4mtJson -Arguments @('resource', 'export', '--package', $importedPackage,
        '--tgi', '0x220557DA:0x00000000:0x0123456789ABCDEF', '--output', $exportedSentinel)
    if (([System.IO.File]::ReadAllBytes($sentinelPath) -join ',') -ne
        ([System.IO.File]::ReadAllBytes($exportedSentinel) -join ',')) {
        throw 'LRLE import changed the unrelated resource payload.'
    }

    $projectRoot = Join-Path $tempRoot 'project'
    New-Item -ItemType Directory -Path $projectRoot | Out-Null
    Copy-Item -LiteralPath $v1Raw -Destination (Join-Path $projectRoot 'v1.bin')
    Copy-Item -LiteralPath $v2Raw -Destination (Join-Path $projectRoot 'v2.bin')
    Write-JsonFile (Join-Path $projectRoot 's4mt.json') ([ordered]@{
        format = 1; name = 'LrleFixture'; packageManifest = 'package.json'; scriptArchives = @()
    })
    $projectManifest = [ordered]@{
        format = 1
        resources = @(
            [ordered]@{ type = '0x2BC04EDF'; group = '0x00000000'; instance = '0x800B7B75A476F5FE'; source = 'v1.bin'; encoding = 'bytes' },
            [ordered]@{ type = '0x2BC04EDF'; group = '0x00000000'; instance = '0x802EB51D443F6176'; source = 'v2.bin'; encoding = 'bytes' }
        )
    }
    $projectManifestPath = Join-Path $projectRoot 'package.json'
    Write-JsonFile $projectManifestPath $projectManifest
    $valid = Invoke-S4mtJson -Arguments @('project', 'validate', '--project', $projectRoot)
    if (-not $valid.valid -or $valid.resourceCount -ne 2) {
        throw 'Project validation rejected real v1/v2 LRLE resources.'
    }

    $projectV1Path = Join-Path $projectRoot 'v1.bin'
    $projectV1Bytes = [System.IO.File]::ReadAllBytes($projectV1Path)
    [System.IO.File]::WriteAllText($projectV1Path, 'LRLE', [System.Text.UTF8Encoding]::new($false))
    $projectManifest.resources[0].encoding = 'utf8'
    Write-JsonFile $projectManifestPath $projectManifest
    $wrongEncoding = Invoke-S4mtJson -Arguments @('project', 'validate', '--project', $projectRoot) -ExpectedExitCode 1
    $encodingDiagnostic = $wrongEncoding.diagnostics | Where-Object {
        $_.code -eq 'invalid_resource_encoding' -and
        $_.path -eq $projectV1Path
    }
    if ($wrongEncoding.valid -ne $false -or $null -eq $encodingDiagnostic) {
        throw 'Project validation accepted UTF-8 encoding for LRLE.'
    }
    $projectManifest.resources[0].encoding = 'bytes'
    Write-JsonFile $projectManifestPath $projectManifest
    [System.IO.File]::WriteAllBytes($projectV1Path, $projectV1Bytes)

    $badPath = Join-Path $projectRoot 'v2.bin'
    $bad = [System.IO.File]::ReadAllBytes($badPath)
    $lastMipOffset = [BitConverter]::ToUInt32($bad, 16 + 8 * 4)
    $badDataStart = 56 + [int][BitConverter]::ToUInt32($bad, 52) * 4
    $bad[$badDataStart + $lastMipOffset] = 0
    [System.IO.File]::WriteAllBytes($badPath, $bad)
    $invalid = Invoke-S4mtJson -Arguments @('project', 'validate', '--project', $projectRoot) -ExpectedExitCode 1
    $diagnostic = $invalid.diagnostics | Where-Object {
        $_.code -eq 'invalid_lrle_image' -and $_.path -eq $badPath -and
        $_.message -match 'mip 8' -and $_.message -match $v2Tgi
    }
    if ($invalid.valid -ne $false -or $null -eq $diagnostic) {
        throw 'Project validation missed the malformed last LRLE mip or its source-path diagnostic.'
    }

    [pscustomobject]@{
        result = 'verified'
        v1 = $v1Tgi
        v2 = $v2Tgi
        dimensions = '1024x2048'
        mips = 9
        builtVersion = 'V002'
        buildPixelExact = $build.identicalPixels -eq $build.totalPixels
        transparentRgbPreserved = $hiddenAlpha.identicalPixels -eq 16 -and $hiddenAlpha.maximumChannelDelta -eq 0
        importPixelExact = $import.identicalPixels -eq $import.totalPixels
        unrelatedPayloadPreserved = $true
        wrongEncodingRejected = $true
        malformedLastMipRejected = $true
    } | ConvertTo-Json -Compress
} finally {
    $resolvedRoot = [System.IO.Path]::GetFullPath($tempRoot)
    $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if (-not $resolvedRoot.StartsWith($resolvedTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete outside the temporary directory: $resolvedRoot"
    }
    if (Test-Path -LiteralPath $resolvedRoot) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
