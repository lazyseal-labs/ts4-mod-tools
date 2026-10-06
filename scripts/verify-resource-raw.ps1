$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourcePackage = 'C:\Program Files\EA Games\The Sims 4\Data\Client\ClientFullBuild0.package'
$tgi = '0x2F7D0004:0x00000000:0x074B5DABFC370F13'
if (-not (Test-Path -LiteralPath $sourcePackage -PathType Leaf)) {
    throw "Fixture package was not found: $sourcePackage"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("s4mt-resource-raw-" + [guid]::NewGuid().ToString('N'))
$payloadPath = Join-Path $tempRoot 'payload.bin'
$sentinelPath = Join-Path $tempRoot 'sentinel.bin'
$manifestPath = Join-Path $tempRoot 'manifest.json'
$basePackage = Join-Path $tempRoot 'base.package'
$outputPackage = Join-Path $tempRoot 'imported.package'
$reExportPath = Join-Path $tempRoot 're-export.bin'

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $export = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- resource export --package $sourcePackage --tgi $tgi --output $payloadPath | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $export.payloadBytes -le 0) {
        throw "Raw resource export failed: $($export | ConvertTo-Json -Compress)"
    }

    [System.IO.File]::WriteAllBytes($sentinelPath, [byte[]](17, 34, 51, 68, 85))
    $manifest = [ordered]@{ format = 1; resources = @(
        [ordered]@{ type = '0x2F7D0004'; group = '0x00000000'; instance = '0x074B5DABFC370F13'; source = 'payload.bin'; encoding = 'bytes' },
        [ordered]@{ type = '0x220557DA'; group = '0x00000000'; instance = '0x0123456789ABCDEF'; source = 'sentinel.bin'; encoding = 'bytes' }
    ) }
    [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    $built = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- build --manifest $manifestPath --output $basePackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $built.verified) {
        throw 'Could not create raw resource import fixture.'
    }

    $import = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- resource import --package $basePackage --tgi $tgi --payload $payloadPath --output $outputPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $import.verified -or $import.typedValidation -or $import.packageResourceCount -ne 2 -or $import.payloadSha256 -ne $export.payloadSha256) {
        throw "Raw resource import did not match the exported payload: $($import | ConvertTo-Json -Compress)"
    }

    $reExport = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- resource export --package $outputPackage --tgi $tgi --output $reExportPath | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $reExport.payloadSha256 -ne $export.payloadSha256) {
        throw 'Raw resource export after import did not match the original payload.'
    }

    [pscustomobject]@{
        result = 'verified'
        tgi = $import.tgi
        payloadBytes = $import.payloadBytes
        payloadSha256 = $import.payloadSha256
        unrelatedResourceCount = $import.packageResourceCount - 1
        payloadOnlyValidation = $import.typedValidation -eq $false
    } | ConvertTo-Json -Compress
} finally {
    $resolvedTempRoot = [System.IO.Path]::GetFullPath($tempRoot)
    $resolvedSystemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if (-not $resolvedTempRoot.StartsWith($resolvedSystemTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete fixture outside the temporary directory: $resolvedTempRoot"
    }
    if (Test-Path -LiteralPath $resolvedTempRoot) {
        Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force
    }
}
