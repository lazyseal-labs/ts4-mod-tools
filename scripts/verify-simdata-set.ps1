$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourcePackage = 'C:\Program Files\EA Games\The Sims 4\Data\Simulation\SimulationFullBuild0.package'
$tgi = '0x545AC67A:0x0003C397:0xE7D7D14BBEA510D9'
if (-not (Test-Path -LiteralPath $sourcePackage -PathType Leaf)) {
    throw "Fixture package was not found: $sourcePackage"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("s4mt-simdata-set-" + [guid]::NewGuid().ToString('N'))
$outputPackage = Join-Path $tempRoot 'edited.package'
$stringOutputPackage = Join-Path $tempRoot 'edited-string.package'
$vectorOutputPackage = Join-Path $tempRoot 'edited-vector.package'
$float4OutputPackage = Join-Path $tempRoot 'edited-float4.package'
$targetValue = '1.25'
$targetString = 's4mt_string_check'
$targetVector = '0.1, 0.2, 0.3'

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    $sourceHash = (Get-FileHash -LiteralPath $sourcePackage -Algorithm SHA256).Hash
    $set = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- simdata set --package $sourcePackage --tgi $tgi --table BuildBuy --row 0 --column BlockAreaLarge --value $targetValue --output $outputPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $set.verified -or $set.tgi -ne $tgi -or $set.table -ne 'BuildBuy' -or $set.row -ne 0 -or $set.column -ne 'BlockAreaLarge' -or $set.type -ne 'FLOAT' -or $set.newValue -ne $targetValue -or $set.packageResourceCount -le 1) {
        throw "SimData scalar edit did not match the game fixture: $($set | ConvertTo-Json -Compress)"
    }

    $vectorSet = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- simdata set --package $sourcePackage --tgi $tgi --table BuildBuy --row 0 --column MagalogHighlight --value $targetVector --output $vectorOutputPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $vectorSet.verified -or $vectorSet.type -ne 'FLOAT3' -or $vectorSet.newValue -ne '0.1,0.2,0.3') {
        throw "SimData FLOAT3 edit did not match the game fixture: $($vectorSet | ConvertTo-Json -Compress)"
    }

    $float4Set = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- simdata set --package $sourcePackage --tgi $tgi --table BuildBuy --row 0 --column EmptyFacetPreviewTintAndOpacity --value '0.1,0.2,0.3,0.4' --output $float4OutputPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $float4Set.verified -or $float4Set.type -ne 'FLOAT4' -or $float4Set.newValue -ne '0.1,0.2,0.3,0.4') {
        throw "SimData FLOAT4 edit did not match the game fixture: $($float4Set | ConvertTo-Json -Compress)"
    }

    $vectorOutputHash = (Get-FileHash -LiteralPath $vectorOutputPackage -Algorithm SHA256).Hash
    $invalidOutput = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- simdata set --package $sourcePackage --tgi $tgi --table BuildBuy --row 0 --column MagalogHighlight --value '0.1,0.2' --output $vectorOutputPackage 2>&1
    $invalidExitCode = $LASTEXITCODE
    $vectorOutputHashAfterInvalid = (Get-FileHash -LiteralPath $vectorOutputPackage -Algorithm SHA256).Hash
    if ($invalidExitCode -ne 2 -or $vectorOutputHashAfterInvalid -ne $vectorOutputHash) {
        throw "A FLOAT3 with the wrong component count was not rejected while preserving the prior package: exit=$invalidExitCode output=$invalidOutput"
    }

    $stringSet = dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- simdata set --package $sourcePackage --tgi $tgi --table BuildBuy --row 0 --column VFX_BlockScreenShake --value $targetString --output $stringOutputPackage | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or -not $stringSet.verified -or $stringSet.type -ne 'STRING8' -or $stringSet.oldValue -eq $targetString -or $stringSet.newValue -ne $targetString) {
        throw "SimData STRING8 edit did not match the game fixture: $($stringSet | ConvertTo-Json -Compress)"
    }

    $stringOutputHash = (Get-FileHash -LiteralPath $stringOutputPackage -Algorithm SHA256).Hash
    $nonAscii = 'caf' + [char]0x00E9
    dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- simdata set --package $sourcePackage --tgi $tgi --table BuildBuy --row 0 --column VFX_BlockScreenShake --value $nonAscii --output $stringOutputPackage 2>$null | Out-Null
    $nonAsciiExitCode = $LASTEXITCODE
    $stringOutputHashAfter = (Get-FileHash -LiteralPath $stringOutputPackage -Algorithm SHA256).Hash
    if ($nonAsciiExitCode -ne 2 -or $stringOutputHashAfter -ne $stringOutputHash) {
        throw 'Non-ASCII STRING8 input was not rejected while preserving the prior package.'
    }

    dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- simdata set --package $sourcePackage --tgi $tgi --table BuildBuy --row 0 --column VFX_ObjectDelete --value $targetString --output $stringOutputPackage 2>$null | Out-Null
    $unreadableStringExitCode = $LASTEXITCODE
    $stringOutputHashAfterUnreadable = (Get-FileHash -LiteralPath $stringOutputPackage -Algorithm SHA256).Hash
    if ($unreadableStringExitCode -ne 2 -or $stringOutputHashAfterUnreadable -ne $stringOutputHash) {
        throw 'An unreadable STRING8 reference was not rejected while preserving the prior package.'
    }

    $sourceHashAfter = (Get-FileHash -LiteralPath $sourcePackage -Algorithm SHA256).Hash
    if ($sourceHashAfter -ne $sourceHash) {
        throw 'SimData scalar edit changed the installed game fixture.'
    }

    [pscustomobject]@{
        result = 'verified'
        tgi = $set.tgi
        table = $set.table
        row = $set.row
        column = $set.column
        type = $set.type
        oldValue = $set.oldValue
        newValue = $set.newValue
        stringType = $stringSet.type
        stringValue = $stringSet.newValue
        fixedVectorType = $vectorSet.type
        fixedVectorValue = $vectorSet.newValue
        float4Type = $float4Set.type
        float4Value = $float4Set.newValue
        nonAsciiRejected = $nonAsciiExitCode -eq 2
        unreadableStringRejected = $unreadableStringExitCode -eq 2
        resourceCount = $set.packageResourceCount
        invalidVectorShapeRejected = $invalidExitCode -eq 2
        sourceUnchanged = $sourceHashAfter -eq $sourceHash
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
