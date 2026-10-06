$ErrorActionPreference = 'Stop'

$s4mtProjectRoot = Split-Path -Parent $PSScriptRoot
$s4mtProject = Join-Path $s4mtProjectRoot 'src\S4MT.Cli'
$s4mtFixtureRoot = Join-Path $env:TEMP ('s4mt-roundtrip-' + [Guid]::NewGuid().ToString('N'))
$s4mtSources = Join-Path $s4mtFixtureRoot 'resources'
New-Item -ItemType Directory -Path $s4mtSources -Force | Out-Null

function Invoke-S4mtJson {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [int]$ExpectedExitCode = 0
    )

    $s4mtOutput = & dotnet run --project $s4mtProject --no-build -- @Arguments 2>&1
    $s4mtExitCode = $LASTEXITCODE
    $s4mtText = ($s4mtOutput | Out-String).Trim()
    if ($s4mtExitCode -ne $ExpectedExitCode) {
        throw "S4MT returned $s4mtExitCode, expected $ExpectedExitCode. Output: $s4mtText"
    }

    if ($ExpectedExitCode -eq 0) {
        return $s4mtText | ConvertFrom-Json
    }

    return $s4mtText | ConvertFrom-Json
}

Push-Location $s4mtProjectRoot
try {
    & dotnet build S4MT.sln --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    $s4mtTuningText = '<I c="Interaction" i="snippet" m="sims4.tuning" n="s4mt_smoke" s="0"><T n="value">1</T></I>'
    $s4mtTuningPath = Join-Path $s4mtSources 'tuning.xml'
    [System.IO.File]::WriteAllText($s4mtTuningPath, $s4mtTuningText, [System.Text.UTF8Encoding]::new($false))

    $s4mtPayloadPath = Join-Path $s4mtSources 'payload.bin'
    $s4mtPayload = [byte[]](0, 1, 127, 128, 255)
    [System.IO.File]::WriteAllBytes($s4mtPayloadPath, $s4mtPayload)

    $s4mtManifestPath = Join-Path $s4mtFixtureRoot 'package.json'
    $s4mtManifest = [ordered]@{
        format = 1
        resources = @(
            [ordered]@{
                type = '0x03B33DDF'
                group = '0x00000000'
                instance = '0x0123456789ABCDEF'
                source = 'resources/tuning.xml'
                encoding = 'utf8'
            },
            [ordered]@{
                type = '0x220557DA'
                group = '0x00000000'
                instance = '0xFEDCBA9876543210'
                source = 'resources/payload.bin'
                encoding = 'bytes'
            }
        )
    }
    [System.IO.File]::WriteAllText(
        $s4mtManifestPath,
        (ConvertTo-Json -InputObject $s4mtManifest -Depth 5),
        [System.Text.UTF8Encoding]::new($false))

    $s4mtPackagePath = Join-Path $s4mtFixtureRoot 'roundtrip.package'
    $s4mtBuild = Invoke-S4mtJson -Arguments @(
        'build', '--manifest', $s4mtManifestPath, '--output', $s4mtPackagePath)
    if ($s4mtBuild.verified -ne $true -or $s4mtBuild.resourceCount -ne 2) {
        throw 'Build did not report a successful two-resource payload round-trip.'
    }

    $s4mtVerify = Invoke-S4mtJson -Arguments @(
        'verify', '--manifest', $s4mtManifestPath, '--package', $s4mtPackagePath)
    if ($s4mtVerify.valid -ne $true -or $s4mtVerify.expectedResourceCount -ne 2 -or
        $s4mtVerify.actualResourceCount -ne 2 -or $s4mtVerify.payloadMismatchKeys.Count -ne 0) {
        throw "Explicit package verification failed: $($s4mtVerify | ConvertTo-Json -Compress -Depth 6)"
    }

    $s4mtInspect = Invoke-S4mtJson -Arguments @(
        'inspect', '--package', $s4mtPackagePath)
    if ($s4mtInspect.resourceCount -ne 2) {
        throw 'Inspect did not report exactly two resources.'
    }

    $s4mtExpected = @(
        '0x03B33DDF:0x00000000:0x0123456789ABCDEF:91',
        '0x220557DA:0x00000000:0xFEDCBA9876543210:5'
    )
    $s4mtActual = @($s4mtInspect.resources | ForEach-Object {
        '{0}:{1}:{2}:{3}' -f $_.type, $_.group, $_.instance, $_.sizeBytes
    })
    if (@(Compare-Object $s4mtExpected $s4mtActual).Count -ne 0) {
        throw "Inspect returned unexpected resource metadata: $($s4mtActual -join ', ')"
    }

    $s4mtPackageHashBefore = (Get-FileHash -LiteralPath $s4mtPackagePath -Algorithm SHA256).Hash
    $s4mtInvalidManifestPath = Join-Path $s4mtFixtureRoot 'invalid.json'
    $s4mtInvalidManifest = [ordered]@{
        format = 1
        resources = @(
            [ordered]@{
                type = '0x03B33DDF'
                group = '0x00000000'
                instance = '0x0123456789ABCDEF'
                source = 'resources/missing.xml'
                encoding = 'utf8'
            }
        )
    }
    [System.IO.File]::WriteAllText(
        $s4mtInvalidManifestPath,
        (ConvertTo-Json -InputObject $s4mtInvalidManifest -Depth 5),
        [System.Text.UTF8Encoding]::new($false))
    $s4mtError = Invoke-S4mtJson -Arguments @(
        'build', '--manifest', $s4mtInvalidManifestPath, '--output', $s4mtPackagePath) -ExpectedExitCode 2
    $s4mtPackageHashAfter = (Get-FileHash -LiteralPath $s4mtPackagePath -Algorithm SHA256).Hash
    if ($s4mtPackageHashBefore -ne $s4mtPackageHashAfter) {
        throw 'An invalid build replaced the prior package.'
    }

    [PSCustomObject]@{
        result = 'verified'
        resourceCount = $s4mtInspect.resourceCount
        payloadsMatched = $s4mtVerify.payloadMismatchKeys.Count -eq 0
        priorOutputPreserved = $true
        fixtureDirectory = $s4mtFixtureRoot
    } | ConvertTo-Json -Compress
}
finally {
    Pop-Location
}
