$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourcePackage = 'C:\Program Files\EA Games\The Sims 4\SP12\Strings_ENG_US.package'
$tgi = '0x220557DA:0x00000000:0x0034C6AB4A0612D5'
if (-not (Test-Path -LiteralPath $sourcePackage -PathType Leaf)) {
    throw "Fixture package was not found: $sourcePackage"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("s4mt-stbl-" + [guid]::NewGuid().ToString('N'))
$editedJson = Join-Path $tempRoot 'edited.json'
$outputPackage = Join-Path $tempRoot 'edited.package'
$builtPackage = Join-Path $tempRoot 'built.package'
$roundTripJson = Join-Path $tempRoot 'round-trip.json'
$builtJson = Join-Path $tempRoot 'built.json'
$unknownLocaleJson = Join-Path $tempRoot 'unknown-locale.json'
$unknownLocalePackage = Join-Path $tempRoot 'unknown-locale.package'
$unknownLocaleExport = Join-Path $tempRoot 'unknown-locale-export.json'

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $sourceHash = (Get-FileHash -LiteralPath $sourcePackage -Algorithm SHA256).Hash

    dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- stbl export --package $sourcePackage --tgi $tgi --output $editedJson | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'STBL export failed.' }
    $document = Get-Content -LiteralPath $editedJson -Raw | ConvertFrom-Json
    if ($document.format -ne 1 -or $document.tgi -ne $tgi -or $document.locale -ne 'en-US' -or $document.entries.Count -lt 1) {
        throw 'STBL export did not preserve its format, TGI, locale, or game fixture entries.'
    }

    $existingKeys = @($document.entries | ForEach-Object { [Convert]::ToUInt32($_.key.Replace('0x', ''), 16) })
    $unicodeKey = 0xA17B2C3D
    while ($existingKeys -contains $unicodeKey) {
        $unicodeKey++
    }
    $existingKeys += $unicodeKey
    $emptyKey = $unicodeKey + 1
    while ($existingKeys -contains $emptyKey) {
        $emptyKey++
    }
    $document.entries += [pscustomobject]@{ key = ('0x{0:X8}' -f $unicodeKey); text = 'S4MT: Zażółć gęślą jaźń — preserved.' }
    $document.entries += [pscustomobject]@{ key = ('0x{0:X8}' -f $emptyKey); text = '' }
    $expectedCount = $document.entries.Count
    $document | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $editedJson -Encoding utf8NoBOM

    $import = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- stbl import --package $sourcePackage --json $editedJson --output $outputPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $import.verified -or $import.locale -ne 'en-US' -or $import.entryCount -ne $expectedCount) {
        throw "STBL import did not verify: $($import | ConvertTo-Json -Compress)"
    }

    $outputHash = (Get-FileHash -LiteralPath $outputPackage -Algorithm SHA256).Hash
    $document.locale = 'pl'
    $document | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $editedJson -Encoding utf8NoBOM
    dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- stbl import --package $sourcePackage --json $editedJson --output $outputPackage 2>$null | Out-Null
    $localeMismatchExitCode = $LASTEXITCODE
    $outputHashAfterMismatch = (Get-FileHash -LiteralPath $outputPackage -Algorithm SHA256).Hash
    if ($localeMismatchExitCode -ne 2 -or $outputHashAfterMismatch -ne $outputHash) {
        throw 'An STBL JSON locale that conflicts with its TGI was not rejected without replacing the package.'
    }
    $document.locale = 'en-US'
    $document | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $editedJson -Encoding utf8NoBOM

    dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- stbl export --package $outputPackage --tgi $tgi --output $roundTripJson | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'STBL read-back export failed.' }
    $roundTrip = Get-Content -LiteralPath $roundTripJson -Raw | ConvertFrom-Json
    $changedEntry = $roundTrip.entries | Where-Object { $_.key -eq ('0x{0:X8}' -f $unicodeKey) } | Select-Object -First 1
    $emptyEntry = $roundTrip.entries | Where-Object { $_.key -eq ('0x{0:X8}' -f $emptyKey) } | Select-Object -First 1
    if ($roundTrip.locale -ne 'en-US' -or $roundTrip.entries.Count -ne $expectedCount -or
        $changedEntry.text -ne 'S4MT: Zażółć gęślą jaźń — preserved.' -or
        $null -eq $emptyEntry -or $emptyEntry.text -ne '') {
        throw 'STBL read-back did not preserve Unicode text, keys, or an empty value.'
    }

    $build = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- stbl build --json $editedJson --output $builtPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $build.verified -or $build.locale -ne 'en-US' -or $build.entryCount -ne $expectedCount) {
        throw "STBL build did not verify: $($build | ConvertTo-Json -Compress)"
    }
    dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- stbl export --package $builtPackage --tgi $tgi --output $builtJson | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'STBL build read-back export failed.' }
    $built = Get-Content -LiteralPath $builtJson -Raw | ConvertFrom-Json
    if ($built.locale -ne 'en-US' -or $built.entries.Count -ne $expectedCount -or
        ($built.entries | Where-Object { $_.key -eq ('0x{0:X8}' -f $emptyKey) }).text -ne '') {
        throw 'STBL build did not preserve the JSON entries.'
    }

    [pscustomobject]@{
        format = 1
        tgi = '0x220557DA:0x00000000:0xFE00000000000001'
        entries = @([pscustomobject]@{ key = '0x00000001'; text = 'Unknown locale fixture' })
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $unknownLocaleJson -Encoding utf8NoBOM
    $unknownBuild = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- stbl build --json $unknownLocaleJson --output $unknownLocalePackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $unknownBuild.verified -or $null -ne $unknownBuild.locale) {
        throw "STBL build did not report an unknown locale as null: $($unknownBuild | ConvertTo-Json -Compress)"
    }
    dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- stbl export --package $unknownLocalePackage --tgi '0x220557DA:0x00000000:0xFE00000000000001' --output $unknownLocaleExport | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'STBL export with an unsupported locale prefix failed.' }
    $unknownExport = Get-Content -LiteralPath $unknownLocaleExport -Raw | ConvertFrom-Json
    if ($null -ne $unknownExport.locale) {
        throw 'STBL export reported a locale for an unsupported instance prefix.'
    }

    if ((Get-FileHash -LiteralPath $sourcePackage -Algorithm SHA256).Hash -ne $sourceHash) {
        throw 'STBL import modified its source package.'
    }

    [pscustomobject]@{
        result = 'verified'
        tgiPreserved = $roundTrip.tgi -eq $tgi
        locale = $roundTrip.locale
        localeMismatchRejected = $localeMismatchExitCode -eq 2
        unknownLocaleReportedAsNull = $null -eq $unknownExport.locale
        entryCount = $roundTrip.entries.Count
        unicodeTextPreserved = $changedEntry.text -eq 'S4MT: Zażółć gęślą jaźń — preserved.'
        emptyTextPreserved = $emptyEntry.text -eq ''
        standaloneBuildVerified = $build.verified
        sourcePackagePreserved = $true
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
