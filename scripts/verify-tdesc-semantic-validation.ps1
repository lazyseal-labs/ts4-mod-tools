$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cliDll = Join-Path $repoRoot 'src\S4MT.Cli\bin\Debug\net10.0\s4mt.dll'
$sourceRoot = Join-Path $repoRoot '.audit\tdesc-semantic\source\Moods\Descriptions'
$fixture = Join-Path $repoRoot '.audit\tdesc-semantic\fixtures\Mood_Stressed.xml'
$gameIndex = Join-Path $env:LOCALAPPDATA 'S4MT\cache\game-index.jsonl.gz'
foreach ($path in @($cliDll, (Join-Path $sourceRoot 'Mood.tdesc'), $fixture)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required local verification input does not exist: $path"
    }
}

function Invoke-S4mt([string[]]$Arguments) {
    $lines = @(& dotnet $script:cliDll @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output = ($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    [pscustomobject]@{ ExitCode = $exitCode; Output = $output }
}

function Invoke-S4mtJson([string[]]$Arguments) {
    $result = Invoke-S4mt $Arguments
    if ($result.ExitCode -ne 0) {
        throw "s4mt $($Arguments -join ' ') failed ($($result.ExitCode)): $($result.Output)"
    }
    return $result.Output | ConvertFrom-Json
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "s4mt-tdesc-semantic-$([Guid]::NewGuid().ToString('N'))"
$tempRootFull = [IO.Path]::GetFullPath($tempRoot)
$tempParentFull = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $tempRootFull.StartsWith($tempParentFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Temporary verification directory escaped the system temp root: $tempRootFull"
}
[IO.Directory]::CreateDirectory($tempRootFull) | Out-Null

try {
    $sourceCopy = Join-Path $tempRootFull 'source'
    $snapshot = Join-Path $tempRootFull 'snapshot'
    [IO.Directory]::CreateDirectory($sourceCopy) | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'Mood.tdesc') -Destination $sourceCopy
    $import = Invoke-S4mtJson @(
        'tdesc', 'refresh', '--from-dir', $sourceCopy,
        '--game-version', '1.128.90.1030', '--source', 'local pinned semantic verification',
        '--output', $snapshot)
    $valid = Invoke-S4mtJson @(
        'tuning', 'validate', '--xml', $fixture,
        '--snapshot', $snapshot, '--game-version', $import.gameVersion,
        '--snapshot-sha256', $import.sha256)
    if (-not $valid.valid -or $valid.schemaValidation.coverage -ne 'partial' -or
        $valid.schemaValidation.checkedValueCount -lt 1) {
        throw 'Known-good game tuning did not pass with useful, explicit partial schema coverage.'
    }

    $badXml = Join-Path $tempRootFull 'bad.xml'
    $xml = [IO.File]::ReadAllText($fixture)
    $xml = $xml.Replace('<T>28</T>', '<T>many</T>')
    [IO.File]::WriteAllText($badXml, $xml, [Text.UTF8Encoding]::new($false))
    $bad = Invoke-S4mt @(
        'tuning', 'validate', '--xml', $badXml,
        '--snapshot', $snapshot, '--game-version', $import.gameVersion,
        '--snapshot-sha256', $import.sha256)
    if ($bad.ExitCode -ne 1 -or $bad.Output -notmatch 'schema_value_invalid') {
        throw 'Malformed integer was not rejected by the pinned semantic validator.'
    }

    $badPin = Invoke-S4mt @(
        'tuning', 'validate', '--xml', $fixture,
        '--snapshot', $snapshot, '--game-version', $import.gameVersion,
        '--snapshot-sha256', ('0' * 64))
    if ($badPin.ExitCode -ne 2 -or $badPin.Output -notmatch 'does not match the requested pin') {
        throw 'Schema digest mismatch was not rejected.'
    }
    $badVersion = Invoke-S4mt @(
        'tuning', 'validate', '--xml', $fixture,
        '--snapshot', $snapshot, '--game-version', '1.127.90.1030',
        '--snapshot-sha256', $import.sha256)
    if ($badVersion.ExitCode -ne 2 -or $badVersion.Output -notmatch 'game version mismatch') {
        throw 'Schema game-version mismatch was not rejected.'
    }

    $badSource = Join-Path $tempRootFull 'malformed-source'
    [IO.Directory]::CreateDirectory($badSource) | Out-Null
    [IO.File]::WriteAllText((Join-Path $badSource 'bad.tdesc'), '<TuningRoot></TuningRoot><Invalid/>')
    $malformedTail = Invoke-S4mt @(
        'tdesc', 'refresh', '--from-dir', $badSource,
        '--game-version', '1.128.90.1030', '--source', 'malformed trailing root test',
        '--output', (Join-Path $tempRootFull 'malformed-snapshot'))
    if ($malformedTail.ExitCode -ne 2) {
        throw 'TDesc importer accepted XML content after the root element.'
    }

    $buildOutput = Join-Path $tempRootFull 'blocked.package'
    $build = Invoke-S4mt @(
        'tuning', 'build', '--tgi', '0x03B33DDF:0x00000000:0x0000000000000001',
        '--xml', $badXml, '--output', $buildOutput,
        '--snapshot', $snapshot, '--game-version', $import.gameVersion,
        '--snapshot-sha256', $import.sha256)
    if ($build.ExitCode -ne 2 -or (Test-Path -LiteralPath $buildOutput)) {
        throw 'Tuning build wrote output despite a schema error.'
    }
    $sourcePackage = Join-Path $tempRootFull 'valid.package'
    $validBuild = Invoke-S4mtJson @(
        'tuning', 'build', '--tgi', '0x03B33DDF:0x00000000:0x0000000000000001',
        '--xml', $fixture, '--output', $sourcePackage,
        '--snapshot', $snapshot, '--game-version', $import.gameVersion,
        '--snapshot-sha256', $import.sha256)
    if (-not $validBuild.verified) {
        throw 'Known-good tuning did not build with the schema pin.'
    }
    $importOutput = Join-Path $tempRootFull 'blocked-import.package'
    $blockedImport = Invoke-S4mt @(
        'tuning', 'import', '--package', $sourcePackage,
        '--tgi', '0x03B33DDF:0x00000000:0x0000000000000001', '--xml', $badXml,
        '--output', $importOutput, '--snapshot', $snapshot,
        '--game-version', $import.gameVersion, '--snapshot-sha256', $import.sha256)
    if ($blockedImport.ExitCode -ne 2 -or (Test-Path -LiteralPath $importOutput)) {
        throw 'Tuning import wrote output despite a schema error.'
    }

    $project = Join-Path $tempRootFull 'project'
    $projectSnapshot = Join-Path $project '.s4mt\schema'
    $resources = Join-Path $project 'resources'
    [IO.Directory]::CreateDirectory($projectSnapshot) | Out-Null
    [IO.Directory]::CreateDirectory($resources) | Out-Null
    Copy-Item -LiteralPath (Join-Path $snapshot 'manifest.json') -Destination $projectSnapshot
    Copy-Item -LiteralPath (Join-Path $snapshot 'index.json') -Destination $projectSnapshot
    Copy-Item -LiteralPath (Join-Path $snapshot 'definitions') -Destination $projectSnapshot -Recurse
    [IO.File]::Copy($badXml, (Join-Path $resources 'bad.xml'))
    [IO.File]::WriteAllBytes((Join-Path $resources 'opaque.bin'), [byte[]](0x53, 0x34, 0x4D, 0x54))
    $projectFile = [ordered]@{
        format = 2; name = 'SchemaSmoke'; packageManifest = 'package.json'; scriptArchives = @()
        tuningSchema = @{ snapshot = '.s4mt/schema'; gameVersion = $import.gameVersion; sha256 = $import.sha256 }
    }
    $manifest = [ordered]@{ format = 1; resources = @(
        @{ type = '0x03B33DDF'; group = '0x00000000'; instance = '0x0000000000000001'; source = 'resources/bad.xml'; encoding = 'utf8' },
        @{ type = '0x12345678'; group = '0x00000000'; instance = '0x0000000000000002'; source = 'resources/opaque.bin'; encoding = 'bytes' }
    ) }
    [IO.File]::WriteAllText((Join-Path $project 's4mt.json'), ($projectFile | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $project 'package.json'), ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $projectValidationResult = Invoke-S4mt @('project', 'validate', '--project', $project)
    if ($projectValidationResult.ExitCode -ne 1) {
        throw 'Project validation did not fail for the schema-invalid tuning value.'
    }
    $projectValidation = $projectValidationResult.Output | ConvertFrom-Json
    if ($projectValidation.valid -or @($projectValidation.diagnostics | Where-Object { $_.code -eq 'schema_value_invalid' }).Count -ne 1 -or
        @($projectValidation.diagnostics | Where-Object { $_.code -eq 'tuning_schema_unverified' }).Count -ne 0) {
        throw 'Project validation did not report and block the schema-invalid tuning value.'
    }
    $projectOutputRoot = Join-Path $tempRootFull 'project-output'
    $projectBuild = Invoke-S4mt @(
        'project', 'build', '--project', $project, '--output-root', $projectOutputRoot)
    if ($projectBuild.ExitCode -ne 1 -or (Test-Path -LiteralPath $projectOutputRoot)) {
        throw 'Project build wrote output despite a schema error.'
    }

    [IO.File]::Copy($fixture, (Join-Path $resources 'bad.xml'), $true)
    $referenceReport = Invoke-S4mtJson @(
        'project', 'validate', '--project', $project, '--game-index', $gameIndex)
    $audioReferences = @($referenceReport.dependencyGraph.observations | Where-Object evidence -eq 'tdesc_resource_key')
    if (-not $referenceReport.valid -or $audioReferences.Count -ne 2 -or
        @($audioReferences | Where-Object resolution -ne 'type_unseen_in_index').Count -ne 0 -or
        @($audioReferences | Where-Object { -not $_.target.StartsWith('0x39B2AA4A:') }).Count -ne 0 -or
        $referenceReport.dependencyGraph.precedenceVerified) {
        throw "Project reference analysis did not preserve the known unindexed audio targets: $($referenceReport | ConvertTo-Json -Depth 12 -Compress)"
    }

    $projectFile.format = 3
    $projectFile.dependencies = @(@{
        source = '0x03B33DDF:0x00000000:0x0000000000000001'
        target = '0x87654321:0x00000000:0x0123456789ABCDEF'
        requirement = 'observed'
        reason = 'External game resource candidate'
    })
    [IO.File]::WriteAllText((Join-Path $project 's4mt.json'), ($projectFile | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $declaredObservation = Invoke-S4mtJson @('project', 'validate', '--project', $project)
    if (-not $declaredObservation.valid -or
        @($declaredObservation.dependencyGraph.observations | Where-Object { $_.evidence -eq 'manifest_assertion' -and $_.requirement -eq 'observed' }).Count -ne 1) {
        throw 'An explicit observed external dependency incorrectly blocked or disappeared from the project report.'
    }
    $projectFile.dependencies[0].requirement = 'bundled'
    $projectFile.dependencies[0].reason = 'Must ship in this package'
    [IO.File]::WriteAllText((Join-Path $project 's4mt.json'), ($projectFile | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $missingBundled = Invoke-S4mt @('project', 'validate', '--project', $project)
    if ($missingBundled.ExitCode -ne 1 -or
        @((($missingBundled.Output | ConvertFrom-Json).diagnostics) | Where-Object code -eq 'bundled_dependency_missing').Count -ne 1) {
        throw 'A missing explicitly bundled dependency did not block project validation.'
    }
    $bundledOutput = Join-Path $tempRootFull 'bundled-output'
    $bundledBuild = Invoke-S4mt @('project', 'build', '--project', $project, '--output-root', $bundledOutput)
    if ($bundledBuild.ExitCode -ne 1 -or (Test-Path -LiteralPath $bundledOutput)) {
        throw 'Project build wrote output despite a missing bundled dependency.'
    }
    $projectFile.dependencies[0].requirement = 'observed'
    [IO.File]::WriteAllText((Join-Path $project 's4mt.json'), ($projectFile | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $manifest.resources = @($manifest.resources | Select-Object -First 1)
    [IO.File]::WriteAllText((Join-Path $project 'package.json'), ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $observedOutput = Join-Path $tempRootFull 'observed-output'
    $observedBuild = Invoke-S4mtJson @('project', 'build', '--project', $project, '--output-root', $observedOutput)
    if (-not $observedBuild.valid -or $observedBuild.resourceCount -ne 1 -or
        @($observedBuild.dependencyGraph.observations | Where-Object evidence -eq 'manifest_assertion').Count -ne 1) {
        throw 'Project build did not include the graph for the exact package input it built.'
    }

    [pscustomobject]@{
        result = 'verified'
        sourceDefinitionCount = $import.classCount
        pinnedGameVersion = $import.gameVersion
        goodTuningCheckedValues = $valid.schemaValidation.checkedValueCount
        goodTuningCoverage = $valid.schemaValidation.coverage
        malformedScalarRejected = $true
        digestMismatchRejected = $true
        gameVersionMismatchRejected = $true
        malformedTrailingXmlRejected = $true
        standaloneBuildBlocked = $true
        standaloneImportBlocked = $true
        projectBuildBlockedBeforeWriting = $true
        projectReferenceGraphFoundAudioTgis = $audioReferences.Count
        unseenAudioResourceTypeRemainsAdvisory = $true
        observedExternalDependencyIsAdvisory = $true
        missingBundledDependencyBlocks = $true
        missingBundledDependencyBlocksBuild = $true
        observedDependencyBuildIncludesGraph = $true
        opaqueUnknownResourceNotMisreportedAsTuning = $true
        snapshotSha256 = $import.sha256
    } | ConvertTo-Json -Depth 8
}
finally {
    if ((Test-Path -LiteralPath $tempRootFull -PathType Container) -and
        $tempRootFull.StartsWith($tempParentFull, [StringComparison]::OrdinalIgnoreCase)) {
        [IO.Directory]::Delete($tempRootFull, $true)
    }
}
