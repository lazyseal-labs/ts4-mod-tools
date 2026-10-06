param(
    [string] $GameRoot = 'C:\Program Files\EA Games\The Sims 4'
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cliProject = Join-Path $repoRoot 'src\S4MT.Cli'
$fixtureManifestPath = Join-Path $PSScriptRoot 'rle-dxt5-fixtures.json'
$fixtureManifest = Get-Content -LiteralPath $fixtureManifestPath -Raw | ConvertFrom-Json
$resolvedGameRoot = [System.IO.Path]::GetFullPath($GameRoot)
$fixturePackage = [System.IO.Path]::GetFullPath((Join-Path $resolvedGameRoot $fixtureManifest.package))
$fixtures = @($fixtureManifest.fixtures)

function Invoke-S4mtJson {
    param([string[]] $Arguments, [int] $ExpectedExitCode = 0)
    $lines = & dotnet run --no-build --project $cliProject -- @Arguments 2>&1
    $exit = $LASTEXITCODE
    if ($exit -ne $ExpectedExitCode) {
        throw "S4MT exited $exit instead of $ExpectedExitCode`: $($lines -join [Environment]::NewLine)"
    }
    return (($lines | Out-String) | ConvertFrom-Json)
}

function Write-JsonFile {
    param([string] $Path, [object] $Value)
    [System.IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 8),
        [System.Text.UTF8Encoding]::new($false))
}

function Assert-ImageProcessingUnsupported {
    param([string[]] $Arguments, [string] $ExpectedText)
    $lines = & dotnet run --no-build --project $cliProject -- @Arguments 2>&1
    if ($LASTEXITCODE -eq 0 -or (($lines | Out-String) -notlike "*$ExpectedText*")) {
        throw "RLE image operation returned an unexpected result: $($lines -join [Environment]::NewLine)"
    }
}

function New-MutatedPayload {
    param([byte[]] $Bytes, [string] $Mutation)
    [byte[]]$copy = $Bytes.Clone()
    switch ($Mutation) {
        'reserved_header' {
            $copy[14] = 1
        }
        'invalid_mip_count' {
            $copy[12] = 0
            $copy[13] = 0
        }
        'truncated_mip_table' {
            $copy = [byte[]]$copy[0..15]
        }
        'out_of_range_offset' {
            [Array]::Copy([BitConverter]::GetBytes([uint32]::MaxValue), 0, $copy, 16, 4)
        }
        'unsupported_opcode' {
            $commandOffset = [BitConverter]::ToUInt32($copy, 16)
            $word = [BitConverter]::ToUInt16($copy, [int]$commandOffset)
            $mutatedWord = [uint16](($word -band 0xFFFC) -bor 3)
            [Array]::Copy([BitConverter]::GetBytes($mutatedWord), 0, $copy, [int]$commandOffset, 2)
        }
        'component_byte_mismatch' {
            $extended = [byte[]]::new($copy.Length + 1)
            [Array]::Copy($copy, $extended, $copy.Length)
            $extended[$copy.Length] = 0
            $copy = $extended
        }
        'mip_block_count_mismatch' {
            $commandOffset = [BitConverter]::ToUInt32($copy, 16)
            $word = [BitConverter]::ToUInt16($copy, [int]$commandOffset)
            $count = $word -shr 2
            if ($count -lt 2) {
                throw 'Pinned first mip command cannot be safely decremented.'
            }
            $mutatedWord = [uint16]((($count - 1) -shl 2) -bor ($word -band 3))
            [Array]::Copy([BitConverter]::GetBytes($mutatedWord), 0, $copy, [int]$commandOffset, 2)
        }
        default { throw "Unknown payload mutation '$Mutation'." }
    }
    return ,$copy
}

function Assert-MalformedProjectPayload {
    param([string] $Tgi, [string] $DiagnosticCode, [string] $Mutation)
    $sourcePath = Join-Path $projectRoot (($Tgi -replace '[^A-Za-z0-9]', '_') + '.bin')
    $original = [System.IO.File]::ReadAllBytes($sourcePath)
    try {
        $changed = New-MutatedPayload -Bytes $original -Mutation $Mutation
        [System.IO.File]::WriteAllBytes($sourcePath, $changed)
        $report = Invoke-S4mtJson @('project', 'validate', '--project', $projectRoot) 1
        $diagnostic = $report.diagnostics | Where-Object {
            $_.code -eq $DiagnosticCode -and $_.path -eq $sourcePath -and $_.message.Contains($Tgi)
        }
        if ($report.valid -or $null -eq $diagnostic) {
            throw "Mutation '$Mutation' for $DiagnosticCode was not rejected with TGI and source path: $($report | ConvertTo-Json -Compress -Depth 8)"
        }
        return [pscustomobject]@{ mutation = $Mutation; code = $diagnostic.code; path = $diagnostic.path }
    }
    finally {
        [System.IO.File]::WriteAllBytes($sourcePath, $original)
    }
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('s4mt-rle-validate-' + [guid]::NewGuid().ToString('N'))
try {
    $rle2FixtureCount = @($fixtures | Where-Object type -eq 'rle2').Count
    $rlesFixtureCount = @($fixtures | Where-Object type -eq 'rles').Count
    if ($fixtures.Count -ne 40 -or $rle2FixtureCount -ne 20 -or $rlesFixtureCount -ne 20 -or
        -not (Test-Path -LiteralPath $fixturePackage -PathType Leaf)) {
        throw "Expected 20 pinned RLE2 fixtures, 20 pinned RLES fixtures, and source package: $fixturePackage"
    }

    $rlesTgi = '0xBA856C78:0x00000000:0x802E66DA49273E35'
    Assert-ImageProcessingUnsupported @('image', 'export', '--package', 'missing.package', '--tgi', $rlesTgi, '--output', 'missing.png') 'RLES image export requires'
    Assert-ImageProcessingUnsupported @('image', 'build', '--tgi', $rlesTgi, '--png', 'missing.png', '--output', 'missing.package') 'RLES image build requires'
    Assert-ImageProcessingUnsupported @('image', 'import', '--package', 'missing.package', '--tgi', $rlesTgi, '--png', 'missing.png', '--output', 'missing.package') 'RLES image import requires'

    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    $projectRoot = Join-Path $tempRoot 'project'
    New-Item -ItemType Directory -Path $projectRoot | Out-Null
    $resources = [System.Collections.Generic.List[object]]::new()

    foreach ($fixture in $fixtures) {
        $tgi = [string]$fixture.tgi
        $fileName = ($tgi -replace '[^A-Za-z0-9]', '_') + '.bin'
        $payloadPath = Join-Path $projectRoot $fileName
        $null = Invoke-S4mtJson @('resource', 'export', '--package', $fixturePackage, '--tgi', $tgi, '--output', $payloadPath)
        $digest = (Get-FileHash -LiteralPath $payloadPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($digest -ne $fixture.sha256) {
            throw "Extracted fixture SHA does not match its pinned anchor: $tgi ($digest)"
        }
        $parts = $tgi.Split(':')
        $resources.Add([ordered]@{
            type = $parts[0]; group = $parts[1]; instance = $parts[2]
            source = $fileName; encoding = 'bytes'
        })
    }

    Write-JsonFile (Join-Path $projectRoot 's4mt.json') ([ordered]@{
        format = 1; name = 'RleStructureFixtures'; packageManifest = 'package.json'; scriptArchives = @()
    })
    $manifestPath = Join-Path $projectRoot 'package.json'
    Write-JsonFile $manifestPath ([ordered]@{ format = 1; resources = @($resources) })
    $valid = Invoke-S4mtJson @('project', 'validate', '--project', $projectRoot)
    if (-not $valid.valid -or $valid.resourceCount -ne $fixtures.Count) {
        throw "Project validation rejected the $($fixtures.Count) real fixtures: $($valid | ConvertTo-Json -Compress -Depth 8)"
    }

    $mutationNames = @(
        'reserved_header', 'invalid_mip_count', 'truncated_mip_table', 'out_of_range_offset',
        'unsupported_opcode', 'component_byte_mismatch', 'mip_block_count_mismatch'
    )
    $mutationEvidence = [System.Collections.Generic.List[object]]::new()
    foreach ($typeFixture in @(
        [pscustomobject]@{ tgi = [string]($fixtures | Where-Object type -eq 'rle2' | Select-Object -First 1).tgi; code = 'invalid_rle2_image' }
        [pscustomobject]@{ tgi = [string]($fixtures | Where-Object type -eq 'rles' | Select-Object -First 1).tgi; code = 'invalid_rles_image' }
    )) {
        foreach ($mutation in $mutationNames) {
            $mutationEvidence.Add((Assert-MalformedProjectPayload -Tgi $typeFixture.tgi `
                -DiagnosticCode $typeFixture.code -Mutation $mutation))
        }
    }
    $restored = Invoke-S4mtJson @('project', 'validate', '--project', $projectRoot)
    if (-not $restored.valid -or $restored.resourceCount -ne $fixtures.Count) {
        throw "Fixture payloads were not restored after malformed-input checks: $($restored | ConvertTo-Json -Compress -Depth 8)"
    }

    Write-Output ([pscustomobject]@{
        validFixtures = $fixtures.Count
        validFixturesPerType = [ordered]@{ rle2 = $rle2FixtureCount; rles = $rlesFixtureCount }
        malformedMutationCases = $mutationEvidence.Count
        malformedMutationsRejected = $true
        rlesSinglePngExportRejected = $true
        rlesSinglePngBuildImportRejected = $true
        mutations = @($mutationEvidence)
    } | ConvertTo-Json -Compress)
}
finally {
    if (Test-Path -LiteralPath $tempRoot -PathType Container) {
        $rootFullPath = [System.IO.Path]::GetFullPath($tempRoot)
        $tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if (-not $rootFullPath.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -or
            [System.IO.Path]::GetFileName($rootFullPath) -notlike 's4mt-rle-validate-*') {
            throw "Refusing cleanup outside the expected temporary directory: $rootFullPath"
        }
        Remove-Item -LiteralPath $rootFullPath -Recurse -Force
    }
}
