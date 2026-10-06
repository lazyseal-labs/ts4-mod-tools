$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$indexPath = 'C:\Users\lasto\AppData\Local\S4MT\cache\game-index.jsonl.gz'
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("s4mt-conflict-" + [guid]::NewGuid().ToString('N'))
$tgi = '0x00B2D882:0x00000000:0x8000BC9A2E2C8DA7'

if (-not (Test-Path -LiteralPath $indexPath -PathType Leaf)) {
    throw "Game index fixture was not found: $indexPath"
}

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $tempRoot 'payload.bin'), [byte[]](1, 2, 3, 4))
    $manifest = [pscustomobject]@{
        format = 1
        resources = @([pscustomobject]@{
            type = '0x00B2D882'
            group = '0x00000000'
            instance = '0x8000BC9A2E2C8DA7'
            source = 'payload.bin'
            encoding = 'bytes'
        })
    }
    $manifestPath = Join-Path $tempRoot 'package.json'
    $manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

    $leftPackage = Join-Path $tempRoot 'left.package'
    $rightPackage = Join-Path $tempRoot 'right.package'
    $left = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- build --manifest $manifestPath --output $leftPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $left.verified) { throw 'Could not build the left conflict fixture.' }
    $right = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- build --manifest $manifestPath --output $rightPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $right.verified) { throw 'Could not build the right conflict fixture.' }

    $gameReport = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- conflict game --package $leftPackage --index $indexPath | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $gameReport.conflictingResourceCount -ne 1 -or $gameReport.conflicts[0].tgi -ne $tgi) {
        throw "Game conflict report did not find the expected TGI: $($gameReport | ConvertTo-Json -Compress)"
    }
    if ($gameReport.precedenceVerified -or $null -ne $gameReport.conflicts[0].effectiveGameProvider) {
        throw 'The report claimed an effective game provider without verified precedence.'
    }

    $packageReport = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- conflict packages --left $leftPackage --right $rightPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $packageReport.sharedResourceCount -ne 1 -or $packageReport.sharedTgis[0] -ne $tgi) {
        throw "Package-to-package report did not find the shared TGI: $($packageReport | ConvertTo-Json -Compress)"
    }

    [pscustomobject]@{
        result = 'verified'
        gameConflictCount = $gameReport.conflictingResourceCount
        gameProviders = $gameReport.conflicts[0].gameProviderCount
        precedenceClaimed = $gameReport.precedenceVerified
        packageSharedTgiCount = $packageReport.sharedResourceCount
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
