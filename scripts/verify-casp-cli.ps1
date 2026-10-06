param(
    [string]$GameRoot = 'C:\Program Files\EA Games\The Sims 4',
    [string]$GamePackage = 'Data\Client\ClientDeltaBuild0.package',
    [string]$Tgi = '034aeecb:00000000:000000000007d39a',
    [string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) '.audit\casp-cli-verification.json')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$cliDll = Join-Path $repoRoot 'src\S4MT.Cli\bin\Debug\net10.0\s4mt.dll'
$sourcePackage = Join-Path $GameRoot $GamePackage
foreach ($requiredPath in @($cliDll, $sourcePackage)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required local verification input does not exist: $requiredPath"
    }
}

function Invoke-S4mt([string[]]$Arguments) {
    $lines = @(& dotnet $script:cliDll @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output = ($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Invoke-S4mtJson([string[]]$Arguments) {
    $result = Invoke-S4mt $Arguments
    if ($result.ExitCode -ne 0) {
        throw "s4mt $($Arguments -join ' ') failed ($($result.ExitCode)): $($result.Output)"
    }
    return $result.Output | ConvertFrom-Json
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "s4mt-casp-verify-$([Guid]::NewGuid().ToString('N'))"
$tempRootFull = [IO.Path]::GetFullPath($tempRoot)
$tempParentFull = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $tempRootFull.StartsWith($tempParentFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Temporary verification directory escaped the system temp root: $tempRootFull"
}
[IO.Directory]::CreateDirectory($tempRootFull) | Out-Null

try {
    $tgiParts = $Tgi.Split(':')
    if ($tgiParts.Length -ne 3) {
        throw "TGI must contain type:group:instance: $Tgi"
    }

    $payloadPath = Join-Path $tempRootFull 'fixture.casp'
    $unrelatedPayloadPath = Join-Path $tempRootFull 'unrelated.bin'
    $manifestPath = Join-Path $tempRootFull 'manifest.json'
    $sourceMiniPackage = Join-Path $tempRootFull 'source.package'
    $renamedMiniPackage = Join-Path $tempRootFull 'renamed.package'
    $beforePayloadPath = Join-Path $tempRootFull 'before.casp'
    $afterPayloadPath = Join-Path $tempRootFull 'after.casp'
    $rejectedOutputPath = Join-Path $tempRootFull 'must-not-exist.package'

    $null = Invoke-S4mtJson @('resource', 'export', '--package', $sourcePackage, '--tgi', $Tgi, '--output', $payloadPath)
    [IO.File]::WriteAllBytes($unrelatedPayloadPath, [byte[]](1, 7, 19, 42, 0, 255))
    $manifest = [ordered]@{
        format = 1
        resources = @(
            [ordered]@{ type = $tgiParts[0]; group = $tgiParts[1]; instance = $tgiParts[2]; source = 'fixture.casp'; encoding = 'bytes' },
            [ordered]@{ type = '0xE882D22F'; group = '0x00000000'; instance = '0x0000000000000001'; source = 'unrelated.bin'; encoding = 'bytes' }
        )
    }
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6))
    $null = Invoke-S4mtJson @('build', '--manifest', $manifestPath, '--output', $sourceMiniPackage)

    $before = Invoke-S4mtJson @('casp', 'inspect', '--package', $sourceMiniPackage, '--tgi', $Tgi)
    $newName = $before.name.Substring(0, $before.name.Length - 4) + 'Blue'
    if ($newName.Length -ne $before.name.Length) {
        throw 'Verification replacement name must have the same character count as the fixture name.'
    }

    $rename = Invoke-S4mtJson @(
        'casp', 'rename', '--package', $sourceMiniPackage, '--tgi', $Tgi,
        '--name', $newName, '--output', $renamedMiniPackage)
    $after = Invoke-S4mtJson @('casp', 'inspect', '--package', $renamedMiniPackage, '--tgi', $Tgi)
    $null = Invoke-S4mtJson @('resource', 'export', '--package', $sourceMiniPackage, '--tgi', $Tgi, '--output', $beforePayloadPath)
    $null = Invoke-S4mtJson @('resource', 'export', '--package', $renamedMiniPackage, '--tgi', $Tgi, '--output', $afterPayloadPath)

    $beforeBytes = [IO.File]::ReadAllBytes($beforePayloadPath)
    $afterBytes = [IO.File]::ReadAllBytes($afterPayloadPath)
    $nameByteLength = [BitConverter]::ToUInt16($beforeBytes, 12)
    $nameOffset = 14
    $expectedBytes = [byte[]]$beforeBytes.Clone()
    $encodedName = [Text.Encoding]::Unicode.GetBytes($newName)
    if ($encodedName.Length -ne $nameByteLength) {
        throw 'Verification replacement does not preserve the encoded name byte length.'
    }
    [Array]::Copy($encodedName, 0, $expectedBytes, $nameOffset, $encodedName.Length)

    if (-not [Linq.Enumerable]::SequenceEqual([byte[]]$expectedBytes, [byte[]]$afterBytes)) {
        throw 'CLI output changed payload bytes outside the name span or encoded the name incorrectly.'
    }
    if (-not $rename.verified -or $after.name -cne $newName -or $after.version -ne 52) {
        throw 'CASP rename report/read-back did not preserve the requested v52 name.'
    }

    $unrelatedId = '0xE882D22F:0x00000000:0x0000000000000001'
    $originalUnrelatedPath = Join-Path $tempRootFull 'unrelated-original.bin'
    $renamedUnrelatedPath = Join-Path $tempRootFull 'unrelated-renamed.bin'
    $null = Invoke-S4mtJson @('resource', 'export', '--package', $sourceMiniPackage, '--tgi', $unrelatedId, '--output', $originalUnrelatedPath)
    $null = Invoke-S4mtJson @('resource', 'export', '--package', $renamedMiniPackage, '--tgi', $unrelatedId, '--output', $renamedUnrelatedPath)
    $unrelatedUnchanged = [Linq.Enumerable]::SequenceEqual(
        [byte[]][IO.File]::ReadAllBytes($originalUnrelatedPath),
        [byte[]][IO.File]::ReadAllBytes($renamedUnrelatedPath))
    if (-not $unrelatedUnchanged) {
        throw 'CASP rename changed the unrelated package resource.'
    }

    $rejected = Invoke-S4mt @(
        'casp', 'rename', '--package', $sourceMiniPackage, '--tgi', $Tgi,
        '--name', ($before.name + 'Longer'), '--output', $rejectedOutputPath)
    if ($rejected.ExitCode -ne 2 -or (Test-Path -LiteralPath $rejectedOutputPath)) {
        throw 'Variable-length rename was not rejected as invalid input before producing output.'
    }

    $report = [ordered]@{
        schemaVersion = 1
        verification = 'casp-cli-v52-fixed-length-rename'
        sourceGameBuild = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $GameRoot 'Game\Bin\TS4_x64.exe')).FileVersion
        sourceTgi = $before.tgi
        version = $before.version
        originalName = $before.name
        renamedName = $after.name
        payloadBytes = $beforeBytes.Length
        nameByteLength = $nameByteLength
        outputMatchesNameSpanPatch = $true
        unrelatedResourcePreserved = $unrelatedUnchanged
        packageReadBackVerified = $rename.verified
        variableLengthRenameRejected = ($rejected.ExitCode -eq 2)
        wroteOnlyToTemporaryDirectory = $true
    }
    $fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($fullOutputPath)) -Force | Out-Null
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fullOutputPath -Encoding utf8
    Get-Content -LiteralPath $fullOutputPath -Raw
}
finally {
    if ((Test-Path -LiteralPath $tempRootFull -PathType Container) -and
        $tempRootFull.StartsWith($tempParentFull, [StringComparison]::OrdinalIgnoreCase)) {
        [IO.Directory]::Delete($tempRootFull, $true)
    }
}
