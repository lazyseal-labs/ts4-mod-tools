$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourcePackage = 'C:\Program Files\EA Games\The Sims 4\Data\Client\ClientDeltaBuild6.package'
$tgi = '0x00B2D882:0x00000000:0x8000BC9A2E2C8DA7'
if (-not (Test-Path -LiteralPath $sourcePackage -PathType Leaf)) {
    throw "Fixture package was not found: $sourcePackage"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("s4mt-image-" + [guid]::NewGuid().ToString('N'))
$png = Join-Path $tempRoot 'source.png'
$builtPackage = Join-Path $tempRoot 'built.package'
$importedPackage = Join-Path $tempRoot 'imported.package'
$outputPng = Join-Path $tempRoot 'output.png'

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $export = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- image export --package $sourcePackage --tgi $tgi --output $png | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $export.width -ne 500 -or $export.height -ne 250) {
        throw "DST image export did not match the game fixture: $($export | ConvertTo-Json -Compress)"
    }

    $build = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- image build --tgi $tgi --png $png --output $builtPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $build.readBackVerified -or $build.width -ne $export.width -or $build.height -ne $export.height) {
        throw "DST image build did not verify its read-back: $($build | ConvertTo-Json -Compress)"
    }
    if ($build.meanChannelDelta -gt 5) {
        throw "DST image read-back mean channel delta exceeded the fixture bound: $($build.meanChannelDelta)"
    }

    $import = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- image import --package $builtPackage --tgi $tgi --png $png --output $importedPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $import.readBackVerified -or $import.packageResourceCount -ne 1) {
        throw "DST image import did not verify: $($import | ConvertTo-Json -Compress)"
    }
    $reexport = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- image export --package $importedPackage --tgi $tgi --output $outputPng | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $reexport.width -ne $export.width -or $reexport.height -ne $export.height) {
        throw 'DST image final export did not preserve image dimensions.'
    }

    [pscustomobject]@{
        result = 'verified'
        tgi = $tgi
        width = $reexport.width
        height = $reexport.height
        readBackMeanChannelDelta = $build.meanChannelDelta
        readBackMaxChannelDelta = $build.maximumChannelDelta
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
