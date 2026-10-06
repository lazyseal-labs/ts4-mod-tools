$ErrorActionPreference = 'Stop'

$s4mtRoot = Split-Path -Parent $PSScriptRoot
$s4mtProject = Join-Path $s4mtRoot 'src\S4MT.Cli'
$s4mtGamePackage = 'C:\Program Files\EA Games\The Sims 4\Data\Simulation\SimulationFullBuild0.package'
$s4mtSimdataTgi = '0x545AC67A:0x0003C397:0xE7D7D14BBEA510D9'
$s4mtDstPackage = 'C:\Program Files\EA Games\The Sims 4\Data\Client\ClientDeltaBuild6.package'
$s4mtDstTgi = '0x00B2D882:0x00000000:0x8000BC9A2E2C8DA7'
$s4mtTjpegPackage = 'C:\Program Files\EA Games\The Sims 4\Data\Client\thumbnails2.package'
$s4mtTjpegTgi = '0x3C2A8647:0x00000000:0x0000000000002960'
$s4mtFixtureRoot = Join-Path $env:TEMP ('s4mt-project-' + [Guid]::NewGuid().ToString('N'))
$s4mtModRoot = Join-Path $s4mtFixtureRoot 'CozyCareers'
$s4mtOutputRoot = Join-Path $s4mtFixtureRoot 'artifacts'

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

    return $s4mtText | ConvertFrom-Json
}

Push-Location $s4mtRoot
try {
    & dotnet build S4MT.sln --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    $s4mtInit = Invoke-S4mtJson -Arguments @(
        'project', 'init', '--name', 'CozyCareers', '--path', $s4mtModRoot)
    $s4mtInitAgain = Invoke-S4mtJson -Arguments @(
        'project', 'init', '--name', 'CozyCareers', '--path', $s4mtModRoot)
    if ($s4mtInit.created -ne $true -or $s4mtInitAgain.created -ne $false) {
        throw 'Project init is not idempotent.'
    }

    $s4mtResources = Join-Path $s4mtModRoot 'resources'
    $s4mtScripts = Join-Path $s4mtModRoot 'scripts'
    $s4mtTuningPath = Join-Path $s4mtResources 'tuning.xml'
    $s4mtStblTgi = '0x220557DA:0x00000000:0xFE00000000000001'
    $s4mtStblJsonPath = Join-Path $s4mtFixtureRoot 'strings.json'
    $s4mtStblPackagePath = Join-Path $s4mtFixtureRoot 'strings-fixture.package'
    $s4mtStblPath = Join-Path $s4mtResources 'strings.stbl'
    [System.IO.File]::WriteAllText(
        $s4mtTuningPath,
        '<I c="Interaction" i="snippet" m="sims4.tuning" n="s4mt_project" s="0"><T n="value">2</T></I>',
        [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllBytes(
        (Join-Path $s4mtResources 'payload.bin'),
        [byte[]](5, 4, 3, 2, 1))
    $s4mtPngPath = Join-Path $s4mtResources 'icon.png'
    $s4mtSimdataPath = Join-Path $s4mtResources 'buildbuy.simdata'
    $s4mtDstPath = Join-Path $s4mtResources 'preview.dst'
    $s4mtTjpegPath = Join-Path $s4mtResources 'thumbnail.tjpeg'
    Add-Type -AssemblyName System.Drawing
    $s4mtBitmap = [System.Drawing.Bitmap]::new(1, 1)
    try {
        $s4mtBitmap.SetPixel(0, 0, [System.Drawing.Color]::FromArgb(255, 255, 0, 0))
        $s4mtBitmap.Save($s4mtPngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $s4mtBitmap.Dispose()
    }
    $s4mtValidPng = [System.IO.File]::ReadAllBytes($s4mtPngPath)

    $s4mtStblDocument = [ordered]@{
        format = 1
        tgi = $s4mtStblTgi
        entries = @([ordered]@{ key = '0x00000001'; text = 'Project validation fixture' })
    }
    [System.IO.File]::WriteAllText(
        $s4mtStblJsonPath,
        (ConvertTo-Json -InputObject $s4mtStblDocument -Depth 5),
        [System.Text.UTF8Encoding]::new($false))
    $s4mtStblBuild = Invoke-S4mtJson -Arguments @(
        'stbl', 'build', '--json', $s4mtStblJsonPath, '--output', $s4mtStblPackagePath)
    if ($s4mtStblBuild.verified -ne $true) {
        throw 'Could not build a valid STBL project-validation fixture.'
    }
    $s4mtStblExport = Invoke-S4mtJson -Arguments @(
        'resource', 'export', '--package', $s4mtStblPackagePath, '--tgi', $s4mtStblTgi, '--output', $s4mtStblPath)
    if ($s4mtStblExport.payloadBytes -le 0) {
        throw 'Could not extract the valid STBL payload for project validation.'
    }

    if (-not (Test-Path -LiteralPath $s4mtGamePackage -PathType Leaf)) {
        throw "SimData validation fixture package was not found: $s4mtGamePackage"
    }
    $s4mtSimdataExport = Invoke-S4mtJson -Arguments @(
        'resource', 'export', '--package', $s4mtGamePackage, '--tgi', $s4mtSimdataTgi, '--output', $s4mtSimdataPath)
    if ($s4mtSimdataExport.payloadBytes -le 0) {
        throw 'Could not extract the valid SimData payload for project validation.'
    }

    if (-not (Test-Path -LiteralPath $s4mtDstPackage -PathType Leaf) -or
        -not (Test-Path -LiteralPath $s4mtTjpegPackage -PathType Leaf)) {
        throw 'Image validation fixture packages were not found in the installed game.'
    }
    $s4mtDstExport = Invoke-S4mtJson -Arguments @(
        'resource', 'export', '--package', $s4mtDstPackage, '--tgi', $s4mtDstTgi, '--output', $s4mtDstPath)
    $s4mtTjpegExport = Invoke-S4mtJson -Arguments @(
        'resource', 'export', '--package', $s4mtTjpegPackage, '--tgi', $s4mtTjpegTgi, '--output', $s4mtTjpegPath)
    if ($s4mtDstExport.payloadBytes -le 0 -or $s4mtTjpegExport.payloadBytes -le 0) {
        throw 'Could not extract valid DST and translucent JPEG payloads for project validation.'
    }

    $s4mtPackageManifest = [ordered]@{
        format = 1
        resources = @(
            [ordered]@{
                type = '0x03B33DDF'
                group = '0x00000000'
                instance = '0x1010101010101010'
                source = 'resources/tuning.xml'
                encoding = 'utf8'
            },
            [ordered]@{
                type = '0x03B33DDF'
                group = '0x00000000'
                instance = '0x2020202020202020'
                source = 'resources/payload.bin'
                encoding = 'bytes'
            },
            [ordered]@{
                type = '0x2F7D0004'
                group = '0x00000000'
                instance = '0x3030303030303030'
                source = 'resources/icon.png'
                encoding = 'bytes'
            },
            [ordered]@{
                type = '0x220557DA'
                group = '0x00000000'
                instance = '0xFE00000000000001'
                source = 'resources/strings.stbl'
                encoding = 'bytes'
            },
            [ordered]@{
                type = '0x545AC67A'
                group = '0x0003C397'
                instance = '0xE7D7D14BBEA510D9'
                source = 'resources/buildbuy.simdata'
                encoding = 'bytes'
            },
            [ordered]@{
                type = '0x00B2D882'
                group = '0x00000000'
                instance = '0x8000BC9A2E2C8DA7'
                source = 'resources/preview.dst'
                encoding = 'bytes'
            },
            [ordered]@{
                type = '0x3C2A8647'
                group = '0x00000000'
                instance = '0x0000000000002960'
                source = 'resources/thumbnail.tjpeg'
                encoding = 'bytes'
            }
        )
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $s4mtModRoot 'package.json'),
        (ConvertTo-Json -InputObject $s4mtPackageManifest -Depth 5),
        [System.Text.UTF8Encoding]::new($false))

    $s4mtPythonPath = Join-Path $s4mtScripts 'Example.pyc'
    [System.IO.File]::WriteAllBytes($s4mtPythonPath, [byte[]](1, 2, 3, 4))
    $s4mtArchivePath = Join-Path $s4mtScripts 'Example.ts4script'
    Compress-Archive -LiteralPath $s4mtPythonPath -DestinationPath $s4mtArchivePath

    $s4mtProjectDocument = [ordered]@{
        format = 1
        name = 'CozyCareers'
        packageManifest = 'package.json'
        scriptArchives = @('scripts/Example.ts4script')
    }
    $s4mtProjectFile = Join-Path $s4mtModRoot 's4mt.json'
    [System.IO.File]::WriteAllText(
        $s4mtProjectFile,
        (ConvertTo-Json -InputObject $s4mtProjectDocument -Depth 5),
        [System.Text.UTF8Encoding]::new($false))

    $s4mtValidation = Invoke-S4mtJson -Arguments @(
        'project', 'validate', '--project', $s4mtModRoot)
    if ($s4mtValidation.valid -ne $true -or $s4mtValidation.resourceCount -ne 7 -or
        $s4mtValidation.scriptArchiveCount -ne 1) {
        throw "Project validation failed: $($s4mtValidation | ConvertTo-Json -Compress -Depth 6)"
    }
    if ($s4mtValidation.diagnostics | Where-Object { $_.code -eq 'simdata_validation_unavailable' }) {
        throw 'Project validation could not round-trip the supported SimData game fixture.'
    }
    if ($s4mtValidation.diagnostics | Where-Object { $_.code -in @('invalid_dst_image', 'invalid_tjpeg_image') }) {
        throw 'Project validation rejected a valid DST or translucent JPEG game fixture.'
    }

    $s4mtOriginalDst = [System.IO.File]::ReadAllBytes($s4mtDstPath)
    [System.IO.File]::WriteAllBytes($s4mtDstPath, [byte[]](5, 4, 3, 2, 1))
    $s4mtInvalidDstValidation = Invoke-S4mtJson -Arguments @(
        'project', 'validate', '--project', $s4mtModRoot) -ExpectedExitCode 1
    $s4mtDstDiagnostic = $s4mtInvalidDstValidation.diagnostics |
        Where-Object { $_.code -eq 'invalid_dst_image' -and $_.path -eq $s4mtDstPath }
    if ($s4mtInvalidDstValidation.valid -ne $false -or $null -eq $s4mtDstDiagnostic) {
        throw 'Project validation did not reject malformed DST data with a source-path diagnostic.'
    }
    [System.IO.File]::WriteAllBytes($s4mtDstPath, $s4mtOriginalDst)

    $s4mtOriginalTjpeg = [System.IO.File]::ReadAllBytes($s4mtTjpegPath)
    [System.IO.File]::WriteAllBytes($s4mtTjpegPath, [byte[]](5, 4, 3, 2, 1))
    $s4mtInvalidTjpegValidation = Invoke-S4mtJson -Arguments @(
        'project', 'validate', '--project', $s4mtModRoot) -ExpectedExitCode 1
    $s4mtTjpegDiagnostic = $s4mtInvalidTjpegValidation.diagnostics |
        Where-Object { $_.code -eq 'invalid_tjpeg_image' -and $_.path -eq $s4mtTjpegPath }
    if ($s4mtInvalidTjpegValidation.valid -ne $false -or $null -eq $s4mtTjpegDiagnostic) {
        throw 'Project validation did not reject malformed translucent JPEG data with a source-path diagnostic.'
    }
    [System.IO.File]::WriteAllBytes($s4mtTjpegPath, $s4mtOriginalTjpeg)

    $s4mtOriginalSimdata = [System.IO.File]::ReadAllBytes($s4mtSimdataPath)
    [System.IO.File]::WriteAllBytes($s4mtSimdataPath, [byte[]](5, 4, 3, 2, 1))
    $s4mtUnvalidatedSimdata = Invoke-S4mtJson -Arguments @(
        'project', 'validate', '--project', $s4mtModRoot)
    $s4mtSimdataWarning = $s4mtUnvalidatedSimdata.diagnostics |
        Where-Object { $_.code -eq 'simdata_validation_unavailable' -and
            $_.severity -eq 'warning' -and $_.path -eq $s4mtSimdataPath }
    if ($s4mtUnvalidatedSimdata.valid -ne $true -or $null -eq $s4mtSimdataWarning) {
        throw 'Project validation did not report unreadable SimData as an opaque-resource warning.'
    }
    [System.IO.File]::WriteAllBytes($s4mtSimdataPath, $s4mtOriginalSimdata)

    $s4mtOriginalStbl = [System.IO.File]::ReadAllBytes($s4mtStblPath)
    [System.IO.File]::WriteAllBytes($s4mtStblPath, [byte[]](5, 4, 3, 2, 1))
    $s4mtInvalidStblValidation = Invoke-S4mtJson -Arguments @(
        'project', 'validate', '--project', $s4mtModRoot) -ExpectedExitCode 1
    $s4mtStblDiagnostic = $s4mtInvalidStblValidation.diagnostics |
        Where-Object { $_.code -eq 'invalid_stbl' -and $_.path -eq $s4mtStblPath }
    if ($s4mtInvalidStblValidation.valid -ne $false -or $null -eq $s4mtStblDiagnostic) {
        throw 'Project validation did not report malformed STBL data at its source path.'
    }
    [System.IO.File]::WriteAllBytes($s4mtStblPath, $s4mtOriginalStbl)

    [System.IO.File]::WriteAllBytes($s4mtPngPath, [byte[]](1, 2, 3, 4))
    $s4mtInvalidPngValidation = Invoke-S4mtJson -Arguments @(
        'project', 'validate', '--project', $s4mtModRoot) -ExpectedExitCode 1
    $s4mtPngDiagnostic = $s4mtInvalidPngValidation.diagnostics |
        Where-Object { $_.code -eq 'invalid_png_image' -and $_.path -eq $s4mtPngPath }
    if ($s4mtInvalidPngValidation.valid -ne $false -or $null -eq $s4mtPngDiagnostic) {
        throw 'Project validation did not report an invalid PNGImage payload at its source path.'
    }
    [System.IO.File]::WriteAllBytes($s4mtPngPath, $s4mtValidPng)

    $s4mtOriginalTuningXml = [System.IO.File]::ReadAllText($s4mtTuningPath)
    [System.IO.File]::WriteAllText(
        $s4mtTuningPath,
        '<I c="Interaction" i="snippet" m="sims4.tuning" n="broken" s="0"><T n="value">',
        [System.Text.UTF8Encoding]::new($false))
    $s4mtInvalidTuningValidation = Invoke-S4mtJson -Arguments @(
        'project', 'validate', '--project', $s4mtModRoot) -ExpectedExitCode 1
    $s4mtTuningDiagnostic = $s4mtInvalidTuningValidation.diagnostics |
        Where-Object { $_.code -eq 'invalid_tuning_xml' -and $_.path -eq $s4mtTuningPath }
    if ($s4mtInvalidTuningValidation.valid -ne $false -or $null -eq $s4mtTuningDiagnostic) {
        throw 'Project validation did not report malformed UTF-8 tuning XML at its source path.'
    }

    $s4mtPackageManifest.resources[0].encoding = 'bytes'
    [System.IO.File]::WriteAllText(
        (Join-Path $s4mtModRoot 'package.json'),
        (ConvertTo-Json -InputObject $s4mtPackageManifest -Depth 5),
        [System.Text.UTF8Encoding]::new($false))
    $s4mtOpaqueTuningValidation = Invoke-S4mtJson -Arguments @(
        'project', 'validate', '--project', $s4mtModRoot)
    if ($s4mtOpaqueTuningValidation.valid -ne $true) {
        throw 'Project validation parsed a tuning resource declared as raw bytes.'
    }

    [System.IO.File]::WriteAllText(
        $s4mtTuningPath,
        $s4mtOriginalTuningXml,
        [System.Text.UTF8Encoding]::new($false))
    $s4mtPackageManifest.resources[0].encoding = 'utf8'
    [System.IO.File]::WriteAllText(
        (Join-Path $s4mtModRoot 'package.json'),
        (ConvertTo-Json -InputObject $s4mtPackageManifest -Depth 5),
        [System.Text.UTF8Encoding]::new($false))

    $s4mtUtf8BinaryCases = @(
        [ordered]@{ name = 'STBL'; resourceIndex = 3; path = $s4mtStblPath },
        [ordered]@{ name = 'SimData'; resourceIndex = 4; path = $s4mtSimdataPath },
        [ordered]@{ name = 'PNGImage'; resourceIndex = 2; path = $s4mtPngPath },
        [ordered]@{ name = 'DSTImage'; resourceIndex = 5; path = $s4mtDstPath },
        [ordered]@{ name = 'translucent JPEG'; resourceIndex = 6; path = $s4mtTjpegPath }
    )
    foreach ($s4mtUtf8BinaryCase in $s4mtUtf8BinaryCases) {
        $s4mtUtf8BinaryResource = $s4mtPackageManifest.resources[$s4mtUtf8BinaryCase.resourceIndex]
        $s4mtOriginalResourceEncoding = $s4mtUtf8BinaryResource.encoding
        $s4mtOriginalResourceBytes = [System.IO.File]::ReadAllBytes($s4mtUtf8BinaryCase.path)
        try {
            [System.IO.File]::WriteAllText(
                $s4mtUtf8BinaryCase.path,
                'valid UTF-8 text declared for a binary resource',
                [System.Text.UTF8Encoding]::new($false))
            $s4mtUtf8BinaryResource.encoding = 'utf8'
            [System.IO.File]::WriteAllText(
                (Join-Path $s4mtModRoot 'package.json'),
                (ConvertTo-Json -InputObject $s4mtPackageManifest -Depth 5),
                [System.Text.UTF8Encoding]::new($false))

            $s4mtUtf8BinaryValidation = Invoke-S4mtJson -Arguments @(
                'project', 'validate', '--project', $s4mtModRoot) -ExpectedExitCode 1
            $s4mtUtf8BinaryDiagnostic = $s4mtUtf8BinaryValidation.diagnostics |
                Where-Object { $_.severity -eq 'error' -and
                    $_.code -eq 'invalid_resource_encoding' -and
                    $_.path -eq $s4mtUtf8BinaryCase.path }
            if ($s4mtUtf8BinaryValidation.valid -ne $false -or $null -eq $s4mtUtf8BinaryDiagnostic) {
                throw "Project validation did not reject UTF-8 $($s4mtUtf8BinaryCase.name) with an encoding diagnostic at its source path."
            }
        }
        finally {
            [System.IO.File]::WriteAllBytes($s4mtUtf8BinaryCase.path, $s4mtOriginalResourceBytes)
            $s4mtUtf8BinaryResource.encoding = $s4mtOriginalResourceEncoding
            [System.IO.File]::WriteAllText(
                (Join-Path $s4mtModRoot 'package.json'),
                (ConvertTo-Json -InputObject $s4mtPackageManifest -Depth 5),
                [System.Text.UTF8Encoding]::new($false))
        }
    }

    $s4mtUnknownUtf8Resource = $s4mtPackageManifest.resources[1]
    $s4mtOriginalUnknownType = $s4mtUnknownUtf8Resource.type
    $s4mtOriginalUnknownEncoding = $s4mtUnknownUtf8Resource.encoding
    $s4mtPayloadPath = Join-Path $s4mtResources 'payload.bin'
    $s4mtOriginalPayloadBytes = [System.IO.File]::ReadAllBytes($s4mtPayloadPath)
    try {
        [System.IO.File]::WriteAllText(
            $s4mtPayloadPath,
            'valid UTF-8 text for an unknown resource',
            [System.Text.UTF8Encoding]::new($false))
        $s4mtUnknownUtf8Resource.type = '0xDEADBEEF'
        $s4mtUnknownUtf8Resource.encoding = 'utf8'
        [System.IO.File]::WriteAllText(
            (Join-Path $s4mtModRoot 'package.json'),
            (ConvertTo-Json -InputObject $s4mtPackageManifest -Depth 5),
            [System.Text.UTF8Encoding]::new($false))

        $s4mtUnknownUtf8Validation = Invoke-S4mtJson -Arguments @(
            'project', 'validate', '--project', $s4mtModRoot)
        $s4mtUnknownEncodingDiagnostic = $s4mtUnknownUtf8Validation.diagnostics |
            Where-Object { $_.code -eq 'invalid_resource_encoding' }
        if ($s4mtUnknownUtf8Validation.valid -ne $true -or $null -ne $s4mtUnknownEncodingDiagnostic) {
            throw 'Project validation rejected an unknown UTF-8 resource.'
        }
    }
    finally {
        [System.IO.File]::WriteAllBytes($s4mtPayloadPath, $s4mtOriginalPayloadBytes)
        $s4mtUnknownUtf8Resource.type = $s4mtOriginalUnknownType
        $s4mtUnknownUtf8Resource.encoding = $s4mtOriginalUnknownEncoding
        [System.IO.File]::WriteAllText(
            (Join-Path $s4mtModRoot 'package.json'),
            (ConvertTo-Json -InputObject $s4mtPackageManifest -Depth 5),
            [System.Text.UTF8Encoding]::new($false))
    }

    $s4mtBuild = Invoke-S4mtJson -Arguments @(
        'project', 'build', '--project', $s4mtModRoot, '--output-root', $s4mtOutputRoot)
    if ($s4mtBuild.valid -ne $true -or $s4mtBuild.reused -ne $false -or
        $s4mtBuild.resourceCount -ne 7 -or $s4mtBuild.scriptArchiveCount -ne 1) {
        throw "Project build failed: $($s4mtBuild | ConvertTo-Json -Compress -Depth 6)"
    }

    $s4mtScriptCopy = Join-Path $s4mtBuild.outputDirectory 'scripts\Example.ts4script'
    $s4mtScriptHash = (Get-FileHash -LiteralPath $s4mtArchivePath -Algorithm SHA256).Hash
    $s4mtCopyHash = (Get-FileHash -LiteralPath $s4mtScriptCopy -Algorithm SHA256).Hash
    if ($s4mtScriptHash -ne $s4mtCopyHash) {
        throw 'Project build changed the .ts4script archive bytes.'
    }

    $s4mtPackageVerification = Invoke-S4mtJson -Arguments @(
        'verify', '--manifest', (Join-Path $s4mtModRoot 'package.json'), '--package', $s4mtBuild.package)
    if ($s4mtPackageVerification.valid -ne $true) {
        throw "Built package failed verification: $($s4mtPackageVerification | ConvertTo-Json -Compress -Depth 6)"
    }

    $s4mtBuildAgain = Invoke-S4mtJson -Arguments @(
        'project', 'build', '--project', $s4mtModRoot, '--output-root', $s4mtOutputRoot)
    if ($s4mtBuildAgain.outputDirectory -ne $s4mtBuild.outputDirectory -or $s4mtBuildAgain.reused -ne $true) {
        throw 'Repeated project build did not converge on the existing verified output.'
    }

    $s4mtInvalidArchivePath = Join-Path $s4mtScripts 'Broken.ts4script'
    [System.IO.File]::WriteAllText($s4mtInvalidArchivePath, 'not a zip archive')
    $s4mtProjectDocument.scriptArchives = @(
        'scripts/Example.ts4script',
        'scripts/Broken.ts4script')
    [System.IO.File]::WriteAllText(
        $s4mtProjectFile,
        (ConvertTo-Json -InputObject $s4mtProjectDocument -Depth 5),
        [System.Text.UTF8Encoding]::new($false))
    $s4mtInvalidValidation = Invoke-S4mtJson -Arguments @(
        'project', 'validate', '--project', $s4mtModRoot) -ExpectedExitCode 1
    if ($s4mtInvalidValidation.valid -ne $false) {
        throw 'Project validation accepted a malformed script archive.'
    }

    [PSCustomObject]@{
        result = 'verified'
        projectInitIdempotent = $true
        packagePayloadsMatched = $s4mtPackageVerification.valid
        scriptArchiveCopiedByteForByte = $true
        repeatBuildReusedOutput = $s4mtBuildAgain.reused
        malformedTuningRejected = $s4mtInvalidTuningValidation.valid -eq $false
        malformedPngRejected = $s4mtInvalidPngValidation.valid -eq $false
        malformedStblRejected = $s4mtInvalidStblValidation.valid -eq $false
        simdataRoundtripValidated = $true
        unreadableSimdataWarnedAsOpaque = $null -ne $s4mtSimdataWarning
        malformedDstRejected = $s4mtInvalidDstValidation.valid -eq $false
        malformedTjpegRejected = $s4mtInvalidTjpegValidation.valid -eq $false
        utf8BinaryResourcesRejected = $true
        unknownUtf8RemainsOpaque = $s4mtUnknownUtf8Validation.valid
        rawTuningBytesRemainOpaque = $s4mtOpaqueTuningValidation.valid
        invalidScriptRejected = $true
        fixtureDirectory = $s4mtFixtureRoot
    } | ConvertTo-Json -Compress
}
finally {
    Pop-Location

    $s4mtResolvedFixtureRoot = [System.IO.Path]::GetFullPath($s4mtFixtureRoot)
    $s4mtResolvedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    $s4mtFixtureRelativePath = [System.IO.Path]::GetRelativePath($s4mtResolvedTempRoot, $s4mtResolvedFixtureRoot)
    if ([System.IO.Path]::IsPathRooted($s4mtFixtureRelativePath) -or
        $s4mtFixtureRelativePath -eq '..' -or
        $s4mtFixtureRelativePath.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Path]::GetFileName($s4mtResolvedFixtureRoot).StartsWith('s4mt-project-', [System.StringComparison]::Ordinal)) {
        throw "Refusing to delete fixture outside the temporary directory: $s4mtResolvedFixtureRoot"
    }
    if (Test-Path -LiteralPath $s4mtResolvedFixtureRoot) {
        Remove-Item -LiteralPath $s4mtResolvedFixtureRoot -Recurse -Force
    }
}
