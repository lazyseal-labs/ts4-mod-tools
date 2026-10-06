$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourcePackage = 'C:\Program Files\EA Games\The Sims 4\Data\Simulation\SimulationFullBuild0.package'
$tgi = '0x62E94D38:0x00000000:0xF1352E740C4710BA'
if (-not (Test-Path -LiteralPath $sourcePackage -PathType Leaf)) {
    throw "Fixture package was not found: $sourcePackage"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("s4mt-tuning-" + [guid]::NewGuid().ToString('N'))
$sourceXml = Join-Path $tempRoot 'source.xml'
$editedPackage = Join-Path $tempRoot 'built.package'
$roundTripPackage = Join-Path $tempRoot 'round-trip.package'
$roundTripXml = Join-Path $tempRoot 'round-trip.xml'

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $sourceHash = (Get-FileHash -LiteralPath $sourcePackage -Algorithm SHA256).Hash

    $export = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- tuning export --package $sourcePackage --tgi $tgi --output $sourceXml | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $export.rootName -ne 'combined') { throw 'CombinedTuning XML export failed.' }
    $validation = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- tuning validate --xml $sourceXml --tgi $tgi | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $validation.wellFormed) { throw 'Real CombinedTuning XML was not well formed.' }

    $hashXml = Join-Path $tempRoot 'hash-check.xml'
    [System.IO.File]::WriteAllText($hashXml, '<I c="Test" i="snippet" m="s4mt" n="s4mt_hash_check" s="3534989495909089743"><T n="value">1</T></I>', [System.Text.UTF8Encoding]::new($false))
    $hashValidation = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- tuning validate --xml $hashXml --tgi '0x03B33DDF:0x00000000:0x310ECE37A46601CF' | ConvertFrom-Json
    $fnv64Candidate = @($hashValidation.nameHashes[0].candidates | Where-Object { $_.algorithm -eq 'FNV64' -and $_.matchesDeclaredInstance -and $_.matchesResourceInstance })
    if ($LASTEXITCODE -ne 0 -or -not $hashValidation.wellFormed -or $fnv64Candidate.Count -ne 1 -or $hashValidation.semanticChecks -notcontains 'declared_s_matches_resource_instance') {
        throw "Tuning name hash did not match its declared and resource IDs: $($hashValidation | ConvertTo-Json -Compress -Depth 6)"
    }

    $build = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- tuning build --tgi $tgi --xml $sourceXml --output $editedPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $build.verified) { throw 'CombinedTuning package build failed its read-back check.' }

    $import = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- tuning import --package $editedPackage --tgi $tgi --xml $sourceXml --output $roundTripPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $import.verified) { throw 'CombinedTuning package import failed its read-back check.' }
    $roundTripExport = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- tuning export --package $roundTripPackage --tgi $tgi --output $roundTripXml | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $roundTripExport.rootName -ne $export.rootName) { throw 'CombinedTuning final export failed.' }
    $roundTripValidation = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- tuning validate --xml $roundTripXml --tgi $tgi | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $roundTripValidation.wellFormed) { throw 'CombinedTuning final XML was not well formed.' }

    if ((Get-FileHash -LiteralPath $sourcePackage -Algorithm SHA256).Hash -ne $sourceHash) {
        throw 'Tuning workflow modified the installed game package.'
    }

    [pscustomobject]@{
        result = 'verified'
        tgi = $tgi
        rootName = $export.rootName
        xmlBytes = (Get-Item -LiteralPath $roundTripXml).Length
        buildVerified = $build.verified
        importVerified = $import.verified
        fnvNameHashVerified = $true
        sourcePackagePreserved = $true
        semanticChecks = $validation.semanticChecks.Count
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
