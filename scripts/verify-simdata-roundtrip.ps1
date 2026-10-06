$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourcePackage = 'C:\Program Files\EA Games\The Sims 4\Data\Simulation\SimulationFullBuild0.package'
$tgi = '0x545AC67A:0x0003C397:0xE7D7D14BBEA510D9'
if (-not (Test-Path -LiteralPath $sourcePackage -PathType Leaf)) {
    throw "Fixture package was not found: $sourcePackage"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("s4mt-simdata-" + [guid]::NewGuid().ToString('N'))
$outputPackage = Join-Path $tempRoot 'round-trip.package'

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $inspect = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- simdata inspect --package $sourcePackage --tgi $tgi | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $inspect.tableCount -ne 2 -or $inspect.rowCount -ne 88) {
        throw "SimData inspection did not match the game fixture: $($inspect | ConvertTo-Json -Compress)"
    }

    $roundtrip = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- simdata roundtrip --package $sourcePackage --tgi $tgi --output $outputPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $roundtrip.verified -or $roundtrip.tableCount -ne $inspect.tableCount -or $roundtrip.rowCount -ne $inspect.rowCount) {
        throw "SimData round-trip did not match the game fixture: $($roundtrip | ConvertTo-Json -Compress)"
    }

    [pscustomobject]@{
        result = 'verified'
        tgi = $roundtrip.tgi
        resourceName = $roundtrip.resourceName
        tableCount = $roundtrip.tableCount
        rowCount = $roundtrip.rowCount
        payloadModelMatched = $roundtrip.verified
        outputPackage = $outputPackage
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
