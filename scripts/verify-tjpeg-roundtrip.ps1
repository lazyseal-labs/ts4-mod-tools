$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourcePackage = 'C:\Program Files\EA Games\The Sims 4\Data\Client\thumbnails2.package'
$tgi = '0x3C2A8647:0x00000000:0x0000000000002960'
if (-not (Test-Path -LiteralPath $sourcePackage -PathType Leaf)) {
    throw "Fixture package was not found: $sourcePackage"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("s4mt-tjpeg-" + [guid]::NewGuid().ToString('N'))
$pngPath = Join-Path $tempRoot 'fixture.png'
$builtPackage = Join-Path $tempRoot 'built.package'
$importedPackage = Join-Path $tempRoot 'imported.package'
$readBackPng = Join-Path $tempRoot 'read-back.png'

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $export = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- image export `
        --package $sourcePackage --tgi $tgi --output $pngPath | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $export.codec -ne 'tjpeg' -or $export.width -lt 1 -or $export.height -lt 1) {
        throw "Translucent JPEG export did not match the game fixture: $($export | ConvertTo-Json -Compress)"
    }

    $build = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- image build `
        --tgi $tgi --png $pngPath --output $builtPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $build.readBackVerified -or
        $build.width -ne $export.width -or $build.height -ne $export.height -or
        $build.meanChannelDelta -gt 3 -or $build.maximumChannelDelta -gt 80) {
        throw "Translucent JPEG build did not verify its read-back: $($build | ConvertTo-Json -Compress)"
    }

    $import = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- image import `
        --package $builtPackage --tgi $tgi --png $pngPath --output $importedPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $import.readBackVerified -or $import.packageResourceCount -ne 1 -or
        $import.meanChannelDelta -gt 3 -or $import.maximumChannelDelta -gt 80) {
        throw "Translucent JPEG import did not verify: $($import | ConvertTo-Json -Compress)"
    }

    $readBack = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- image export `
        --package $importedPackage --tgi $tgi --output $readBackPng | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $readBack.codec -ne 'tjpeg' -or
        $readBack.width -ne $export.width -or $readBack.height -ne $export.height) {
        throw 'Translucent JPEG final export changed image dimensions or codec.'
    }

    [pscustomobject]@{
        result = 'verified'
        tgi = $tgi
        codec = $export.codec
        width = $readBack.width
        height = $readBack.height
        buildMeanChannelDelta = $build.meanChannelDelta
        buildMaxChannelDelta = $build.maximumChannelDelta
        importMeanChannelDelta = $import.meanChannelDelta
        importMaxChannelDelta = $import.maximumChannelDelta
        buildVerified = $build.readBackVerified
        importVerified = $import.readBackVerified
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
