$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cliProject = Join-Path $repoRoot 'src\S4MT.Cli'
$stblSource = 'C:\Program Files\EA Games\The Sims 4\SP12\Strings_ENG_US.package'
$stblTgi = '0x220557DA:0x00000000:0x0034C6AB4A0612D5'
$tuningSource = 'C:\Program Files\EA Games\The Sims 4\Data\Simulation\SimulationFullBuild0.package'
$tuningTgi = '0x62E94D38:0x00000000:0xF1352E740C4710BA'
$imageSource = 'C:\Program Files\EA Games\The Sims 4\Data\Client\ClientFullBuild0.package'
$imageTgi = '0x2F7D0004:0x00000000:0x074B5DABFC370F13'

foreach ($fixture in @($stblSource, $tuningSource, $imageSource)) {
    if (-not (Test-Path -LiteralPath $fixture -PathType Leaf)) {
        throw "Fixture package was not found: $fixture"
    }
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("s4mt-path-collisions-" + [guid]::NewGuid().ToString('N'))
$stblJson = Join-Path $tempRoot 'table.json'
$stblPackage = Join-Path $tempRoot 'table.package'
$tuningXml = Join-Path $tempRoot 'tuning.xml'
$tuningPackage = Join-Path $tempRoot 'tuning.package'
$png = Join-Path $tempRoot 'image.png'
$imagePackage = Join-Path $tempRoot 'image.package'

function Invoke-CliJson([string[]]$Arguments, [int]$ExpectedExitCode = 0) {
    $output = & dotnet run --no-build --project $cliProject -- @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne $ExpectedExitCode) {
        throw "CLI command returned $exitCode, expected ${ExpectedExitCode}: $($Arguments -join ' ')"
    }
    return ($output -join "`n" | ConvertFrom-Json)
}

function Assert-RejectedWithoutChanging([string]$Name, [string[]]$Arguments, [string[]]$ProtectedPaths) {
    $before = @{}
    foreach ($path in $ProtectedPaths) {
        $before[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }

    & dotnet run --no-build --project $cliProject -- @Arguments *> $null
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        throw "Expected output-path collision to be rejected: $Name"
    }

    foreach ($path in $ProtectedPaths) {
        $after = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($after -ne $before[$path]) {
            throw "Rejected collision changed an input file: $Name ($path)"
        }
    }
}

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null

    Invoke-CliJson @('stbl', 'export', '--package', $stblSource, '--tgi', $stblTgi, '--output', $stblJson) | Out-Null
    Invoke-CliJson @('stbl', 'build', '--json', $stblJson, '--output', $stblPackage) | Out-Null
    Invoke-CliJson @('tuning', 'export', '--package', $tuningSource, '--tgi', $tuningTgi, '--output', $tuningXml) | Out-Null
    Invoke-CliJson @('tuning', 'build', '--tgi', $tuningTgi, '--xml', $tuningXml, '--output', $tuningPackage) | Out-Null
    Invoke-CliJson @('image', 'export', '--package', $imageSource, '--tgi', $imageTgi, '--output', $png) | Out-Null
    Invoke-CliJson @('image', 'build', '--tgi', $imageTgi, '--png', $png, '--output', $imagePackage) | Out-Null

    $checks = @(
        @{ name = 'image export/package'; args = @('image', 'export', '--package', $imagePackage, '--tgi', $imageTgi, '--output', $imagePackage); paths = @($imagePackage) },
        @{ name = 'image build/PNG'; args = @('image', 'build', '--tgi', $imageTgi, '--png', $png, '--output', $png); paths = @($png) },
        @{ name = 'image import/package'; args = @('image', 'import', '--package', $imagePackage, '--tgi', $imageTgi, '--png', $png, '--output', $imagePackage); paths = @($imagePackage, $png) },
        @{ name = 'image import/PNG'; args = @('image', 'import', '--package', $imagePackage, '--tgi', $imageTgi, '--png', $png, '--output', $png); paths = @($imagePackage, $png) },
        @{ name = 'STBL export/package'; args = @('stbl', 'export', '--package', $stblPackage, '--tgi', $stblTgi, '--output', $stblPackage); paths = @($stblPackage) },
        @{ name = 'STBL build/JSON'; args = @('stbl', 'build', '--json', $stblJson, '--output', $stblJson); paths = @($stblJson) },
        @{ name = 'STBL import/package'; args = @('stbl', 'import', '--package', $stblPackage, '--json', $stblJson, '--output', $stblPackage); paths = @($stblPackage, $stblJson) },
        @{ name = 'STBL import/JSON'; args = @('stbl', 'import', '--package', $stblPackage, '--json', $stblJson, '--output', $stblJson); paths = @($stblPackage, $stblJson) },
        @{ name = 'tuning export/package'; args = @('tuning', 'export', '--package', $tuningPackage, '--tgi', $tuningTgi, '--output', $tuningPackage); paths = @($tuningPackage) },
        @{ name = 'tuning build/XML'; args = @('tuning', 'build', '--tgi', $tuningTgi, '--xml', $tuningXml, '--output', $tuningXml); paths = @($tuningXml) },
        @{ name = 'tuning import/package'; args = @('tuning', 'import', '--package', $tuningPackage, '--tgi', $tuningTgi, '--xml', $tuningXml, '--output', $tuningPackage); paths = @($tuningPackage, $tuningXml) },
        @{ name = 'tuning import/XML'; args = @('tuning', 'import', '--package', $tuningPackage, '--tgi', $tuningTgi, '--xml', $tuningXml, '--output', $tuningXml); paths = @($tuningPackage, $tuningXml) }
    )

    foreach ($check in $checks) {
        Assert-RejectedWithoutChanging $check.name $check.args $check.paths
    }

    $aliasDirectory = Join-Path $tempRoot 'alias'
    New-Item -ItemType Junction -Path $aliasDirectory -Target $tempRoot | Out-Null
    Assert-RejectedWithoutChanging 'image export/junction alias' @(
        'image', 'export', '--package', $imagePackage, '--tgi', $imageTgi,
        '--output', (Join-Path $aliasDirectory 'image.package')) @($imagePackage)

    $resourceManifestPath = Join-Path $tempRoot 'resource-manifest.json'
    $resourceManifest = [ordered]@{
        format = 1
        resources = @(
            [ordered]@{
                type = '0x220557DA'
                group = '0x00000000'
                instance = '0x1111111111111111'
                source = 'image.png'
                encoding = 'bytes'
            }
        )
    }
    [System.IO.File]::WriteAllText(
        $resourceManifestPath,
        ($resourceManifest | ConvertTo-Json -Depth 5),
        [System.Text.UTF8Encoding]::new($false))
    Assert-RejectedWithoutChanging 'package build/source junction alias' @(
        'build', '--manifest', $resourceManifestPath, '--output', (Join-Path $aliasDirectory 'image.png')) @($png)
    Assert-RejectedWithoutChanging 'package build/manifest junction alias' @(
        'build', '--manifest', $resourceManifestPath, '--output', (Join-Path $aliasDirectory 'resource-manifest.json')) @($resourceManifestPath)

    $mergeManifestPath = Join-Path $tempRoot 'merge-manifest.json'
    $mergeManifest = [ordered]@{ format = 1; packages = @('image.package') }
    [System.IO.File]::WriteAllText(
        $mergeManifestPath,
        ($mergeManifest | ConvertTo-Json -Depth 5),
        [System.Text.UTF8Encoding]::new($false))
    Assert-RejectedWithoutChanging 'package merge/input junction alias' @(
        'package', 'merge', '--manifest', $mergeManifestPath, '--output', (Join-Path $aliasDirectory 'image.package')) @($imagePackage)

    Assert-RejectedWithoutChanging 'resource export/package junction alias' @(
        'resource', 'export', '--package', $imagePackage, '--tgi', $imageTgi,
        '--output', (Join-Path $aliasDirectory 'image.package')) @($imagePackage)
    Assert-RejectedWithoutChanging 'resource import/payload junction alias' @(
        'resource', 'import', '--package', $imagePackage, '--tgi', $imageTgi, '--payload', $png,
        '--output', (Join-Path $aliasDirectory 'image.png')) @($imagePackage, $png)

    $projectRoot = Join-Path $tempRoot 'project'
    $outsideRoot = Join-Path $tempRoot 'outside'
    $projectLink = Join-Path $projectRoot 'linked'
    New-Item -ItemType Directory -Force -Path $projectRoot, $outsideRoot | Out-Null
    New-Item -ItemType Junction -Path $projectLink -Target $outsideRoot | Out-Null
    [System.IO.File]::WriteAllBytes((Join-Path $outsideRoot 'payload.bin'), [byte[]](1, 2, 3))
    $projectManifest = [ordered]@{
        format = 1
        resources = @(
            [ordered]@{
                type = '0x220557DA'
                group = '0x00000000'
                instance = '0x2222222222222222'
                source = 'linked/payload.bin'
                encoding = 'bytes'
            }
        )
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $projectRoot 'package.json'),
        ($projectManifest | ConvertTo-Json -Depth 5),
        [System.Text.UTF8Encoding]::new($false))
    $projectDocument = [ordered]@{
        format = 1
        name = 'PathBoundary'
        packageManifest = 'package.json'
        scriptArchives = @()
    }
    $projectFile = Join-Path $projectRoot 's4mt.json'
    [System.IO.File]::WriteAllText(
        $projectFile,
        ($projectDocument | ConvertTo-Json -Depth 5),
        [System.Text.UTF8Encoding]::new($false))
    $sourceEscapeReport = Invoke-CliJson @('project', 'validate', '--project', $projectRoot) 1
    if ($sourceEscapeReport.valid -ne $false) {
        throw 'Project validation accepted a source that resolves through a junction outside the project.'
    }

    $projectManifest.resources = @()
    [System.IO.File]::WriteAllText(
        (Join-Path $projectRoot 'package.json'),
        ($projectManifest | ConvertTo-Json -Depth 5),
        [System.Text.UTF8Encoding]::new($false))
    $projectDocument.scriptArchives = @('linked/Example.ts4script')
    [System.IO.File]::WriteAllText(
        $projectFile,
        ($projectDocument | ConvertTo-Json -Depth 5),
        [System.Text.UTF8Encoding]::new($false))
    $archiveEscapeReport = Invoke-CliJson @('project', 'validate', '--project', $projectRoot) 1
    if ($archiveEscapeReport.valid -ne $false) {
        throw 'Project validation accepted a script archive that resolves through a junction outside the project.'
    }

    Assert-RejectedWithoutChanging 'game index/junction alias inside game root' @(
        'game', 'index', '--game-root', $projectRoot,
        '--output', (Join-Path $aliasDirectory 'project\game-index.jsonl.gz')) @($projectFile)

    $shortNameLongPath = 'C:\Program Files\EA Games\The Sims 4\Game\Bin\TS4_x64.exe'
    $shortNameAliasPath = 'C:\PROGRA~1\EA Games\The Sims 4\Game\Bin\TS4_x64.exe'
    $shortNameAliasTested = Test-Path -LiteralPath $shortNameAliasPath -PathType Leaf
    if ($shortNameAliasTested) {
        $shortNameManifestPath = Join-Path $tempRoot 'short-name-merge.json'
        [System.IO.File]::WriteAllText(
            $shortNameManifestPath,
            ([ordered]@{ format = 1; packages = @($shortNameLongPath) } | ConvertTo-Json -Depth 5),
            [System.Text.UTF8Encoding]::new($false))
        $shortNameInputHash = (Get-FileHash -LiteralPath $shortNameLongPath -Algorithm SHA256).Hash
        $shortNameOutput = & dotnet run --no-build --project $cliProject -- package merge `
            --manifest $shortNameManifestPath --output $shortNameAliasPath 2>&1
        $shortNameExitCode = $LASTEXITCODE
        $shortNameMessage = $shortNameOutput | Out-String
        if ($shortNameExitCode -eq 0 -or $shortNameMessage -notmatch 'input package') {
            throw "NTFS short-name output collision was not rejected by the path guard: $shortNameMessage"
        }
        if ((Get-FileHash -LiteralPath $shortNameLongPath -Algorithm SHA256).Hash -ne $shortNameInputHash) {
            throw 'NTFS short-name collision changed the protected input file.'
        }
    }

    [pscustomobject]@{
        result = 'verified'
        collisionCasesRejected = $checks.Count + 7 + [int]$shortNameAliasTested
        junctionAliasRejected = $true
        ntfsShortNameAliasRejected = $shortNameAliasTested
        projectJunctionCasesRejected = 2
        projectSourceAndArchiveJunctionsRejected = $true
        protectedInputsPreserved = $true
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
