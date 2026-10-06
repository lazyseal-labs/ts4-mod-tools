$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packagePath = 'C:\Program Files\EA Games\The Sims 4\SP81\Strings_POL_PL.package'
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "Fixture package was not found: $packagePath"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("s4mt-game-index-" + [guid]::NewGuid().ToString('N'))
$gameRoot = Join-Path $tempRoot 'game'
$indexPath = Join-Path $tempRoot 'cache\game-index.jsonl.gz'
$outputDirectory = [System.IO.Path]::GetDirectoryName($indexPath)
$tgi = '0x220557DA:0x00000000:0x0FECE64508663B7D'

try {
    New-Item -ItemType Directory -Force -Path (Join-Path $gameRoot 'SP81') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $gameRoot 'Delta\SP81') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $gameRoot 'Data') | Out-Null
    Copy-Item -LiteralPath $packagePath -Destination (Join-Path $gameRoot 'SP81\Strings_POL_PL.package')
    Copy-Item -LiteralPath $packagePath -Destination (Join-Path $gameRoot 'Delta\SP81\Strings_POL_PL.package')
    Copy-Item -LiteralPath $packagePath -Destination (Join-Path $gameRoot 'Data\ClientDeltaBuild0.package')

    $index1 = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- game index --game-root $gameRoot --output $indexPath | ConvertFrom-Json
    if ($index1.packageCount -ne 3 -or $index1.resourceCount -ne 3 -or $index1.reused -ne $false) {
        throw "Unexpected first index result: $($index1 | ConvertTo-Json -Compress)"
    }

    $query = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- game query --index $indexPath --tgi $tgi | ConvertFrom-Json
    if ($query.resolution -ne 'multiple_providers' -or $query.providers.Count -ne 3 -or $query.precedenceVerified) {
        throw "Unexpected duplicate query result: $($query | ConvertTo-Json -Compress)"
    }
    if (($query.providers | Where-Object { $_.layer -eq 'patch' }).Count -ne 2) {
        throw 'Directory and DeltaBuild-file patch providers were not retained in the query result.'
    }

    $find = dotnet run --no-build --project (Join-Path $repoRoot 'src\\S4MT.Cli') -- game find --index $indexPath --type 0x220557DA --limit 5 | ConvertFrom-Json
    if ($find.matchCount -ne 3 -or ($find.resources | Where-Object { $_.layer -eq 'patch' }).Count -ne 2) {
        throw "Type search did not return both layers: $($find | ConvertTo-Json -Compress)"
    }

    $index2 = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- game index --game-root $gameRoot --output $indexPath | ConvertFrom-Json
    if ($index2.reused -ne $true -or $index2.fingerprint -ne $index1.fingerprint) {
        throw "Unchanged index was not reused: $($index2 | ConvertTo-Json -Compress)"
    }

    $priorHash = (Get-FileHash -LiteralPath $indexPath -Algorithm SHA256).Hash
    $missingRoot = Join-Path $tempRoot 'missing-game'
    dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- game index --game-root $missingRoot --output $indexPath 2>$null | Out-Null
    $failedExitCode = $LASTEXITCODE
    if ($failedExitCode -eq 0) {
        throw 'Indexing a nonexistent game directory unexpectedly succeeded.'
    }
    $afterHash = (Get-FileHash -LiteralPath $indexPath -Algorithm SHA256).Hash
    if ($priorHash -ne $afterHash) {
        throw 'Failed indexing replaced the previous cache.'
    }

    [pscustomobject]@{
        result = 'verified'
        packageCount = $index1.packageCount
        resourceCount = $index1.resourceCount
        duplicateProvidersRetained = $query.providers.Count
        typeSearchCount = $find.matchCount
        deltaProviderRetained = $true
        precedenceClaimed = $query.precedenceVerified
        unchangedIndexReused = $index2.reused
        invalidRootPreservedPriorCache = $true
        fixtureDirectory = $tempRoot
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
