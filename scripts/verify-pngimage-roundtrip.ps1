$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourcePackage = 'C:\Program Files\EA Games\The Sims 4\Data\Client\ClientFullBuild0.package'
$tgi = '0x2F7D0004:0x00000000:0x074B5DABFC370F13'
if (-not (Test-Path -LiteralPath $sourcePackage -PathType Leaf)) {
    throw "Fixture package was not found: $sourcePackage"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("s4mt-pngimage-" + [guid]::NewGuid().ToString('N'))
$pngPath = Join-Path $tempRoot 'fixture.png'
$manifestPath = Join-Path $tempRoot 'manifest.json'
$sentinelPath = Join-Path $tempRoot 'sentinel.bin'
$builtPackage = Join-Path $tempRoot 'built.package'
$basePackage = Join-Path $tempRoot 'base.package'
$importedPackage = Join-Path $tempRoot 'imported.package'

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $export = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- image export --package $sourcePackage --tgi $tgi --output $pngPath | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $export.codec -ne 'png' -or $export.width -ne 640 -or $export.height -ne 640) {
        throw "PNGImage export did not match the game fixture: $($export | ConvertTo-Json -Compress)"
    }

    $build = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- image build --tgi $tgi --png $pngPath --output $builtPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $build.readBackVerified -or $build.identicalPixels -ne $build.totalPixels -or $build.maximumChannelDelta -ne 0) {
        throw "PNGImage build was not pixel-exact: $($build | ConvertTo-Json -Compress)"
    }

    [System.IO.File]::WriteAllBytes($sentinelPath, [byte[]](17, 34, 51, 68, 85))
    $manifest = [ordered]@{
        format = 1
        resources = @(
            [ordered]@{ type = '0x2F7D0004'; group = '0x00000000'; instance = '0x074B5DABFC370F13'; source = 'fixture.png'; encoding = 'bytes' },
            [ordered]@{ type = '0x220557DA'; group = '0x00000000'; instance = '0x0123456789ABCDEF'; source = 'sentinel.bin'; encoding = 'bytes' }
        )
    }
    [System.IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))
    $packageBuild = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- build --manifest $manifestPath --output $basePackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $packageBuild.verified -or $packageBuild.resourceCount -ne 2) {
        throw "PNGImage package fixture could not be created: $($packageBuild | ConvertTo-Json -Compress)"
    }

    $import = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- image import --package $basePackage --tgi $tgi --png $pngPath --output $importedPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $import.readBackVerified -or $import.packageResourceCount -ne 2 -or $import.identicalPixels -ne $import.totalPixels -or $import.maximumChannelDelta -ne 0) {
        throw "PNGImage import did not preserve pixels and unrelated resource: $($import | ConvertTo-Json -Compress)"
    }

    [pscustomobject]@{
        result = 'verified'
        tgi = $export.tgi
        width = $export.width
        height = $export.height
        pngPayloadBytes = $export.pngBytes
        buildPixelExact = $build.identicalPixels -eq $build.totalPixels
        importPixelExact = $import.identicalPixels -eq $import.totalPixels
        unrelatedResourceCount = $import.packageResourceCount - 1
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
