$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cliProject = Join-Path $repoRoot 'src\S4MT.Cli'
$fullBuildPackage = 'C:\Program Files\EA Games\The Sims 4\Data\Simulation\SimulationFullBuild0.package'
$deltaBuildPackage = 'C:\Program Files\EA Games\The Sims 4\Data\Simulation\SimulationDeltaBuild0.package'
$combinedTgi = '0x62E94D38:0x00000000:0xF1352E740C4710BA'
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('s4mt-shared-index-' + [Guid]::NewGuid().ToString('N'))
$projectRoot = Join-Path $fixtureRoot 'CombinedIndexMod'
$resourceRoot = Join-Path $projectRoot 'resources'
$outputRoot = Join-Path $fixtureRoot 'artifacts'

function Invoke-S4mtJson {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [int]$ExpectedExitCode = 0
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in (@('run', '--no-build', '--project', $cliProject, '--') + $Arguments)) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    [void]$process.Start()
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    if ($null -eq $stdout) { $stdout = '' }
    if ($null -eq $stderr) { $stderr = '' }
    $stdout = $stdout.Trim()
    $stderr = $stderr.Trim()
    $process.Dispose()
    if ($exitCode -ne $ExpectedExitCode) {
        throw "S4MT returned $exitCode, expected $ExpectedExitCode. stdout: $stdout stderr: $stderr"
    }
    $text = if ($stdout.Length -gt 0) { $stdout } else { $stderr }
    try {
        return $text | ConvertFrom-Json
    }
    catch {
        throw "S4MT did not return JSON. Output: $text"
    }
}

function Write-Utf8File {
    param([string]$Path, [string]$Content)
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Assert-SharedIndex {
    param(
        [string]$Path,
        [string]$ExpectedStatus,
        [int]$ExpectedIssueCount,
        [int]$ExpectedExitCode = 0
    )

    $result = Invoke-S4mtJson -Arguments @(
        'tuning', 'validate', '--xml', $Path, '--tgi', $combinedTgi) -ExpectedExitCode $ExpectedExitCode
    if ($result.wellFormed -ne $true -or $result.valid -ne ($ExpectedStatus -eq 'valid') -or
        $result.sharedIndexValidation.status -ne $ExpectedStatus -or
        $result.sharedIndexValidation.issueCount -ne $ExpectedIssueCount) {
        throw "Unexpected shared-index result: $($result | ConvertTo-Json -Compress -Depth 6)"
    }
    return $result
}

try {
    if (-not (Test-Path -LiteralPath $fullBuildPackage -PathType Leaf) -or
        -not (Test-Path -LiteralPath $deltaBuildPackage -PathType Leaf)) {
        throw 'The installed-game CombinedTuning fixture packages were not found.'
    }
    & dotnet build (Join-Path $repoRoot 'S4MT.sln') --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }

    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    $realXmlPath = Join-Path $fixtureRoot 'full-build.xml'
    $export = Invoke-S4mtJson -Arguments @(
        'tuning', 'export', '--package', $fullBuildPackage, '--tgi', $combinedTgi, '--output', $realXmlPath)
    if ($export.rootName -ne 'combined') {
        throw 'The real XML fixture did not export as a CombinedTuning document.'
    }
    $realValidation = Assert-SharedIndex -Path $realXmlPath -ExpectedStatus 'valid' -ExpectedIssueCount 0
    if ($realValidation.sharedIndexValidation.sharedTableCount -ne 1 -or
        $realValidation.sharedIndexValidation.definitionCount -ne 29541 -or
        $realValidation.sharedIndexValidation.referenceCount -ne 260536) {
        throw "Real CombinedTuning counts changed: $($realValidation.sharedIndexValidation | ConvertTo-Json -Compress -Depth 4)"
    }

    $validXmlPath = Join-Path $fixtureRoot 'valid.xml'
    Write-Utf8File $validXmlPath '<combined><g><E x="a"/><T x="b">value</T></g><I><r x="a"/><r x="b"/></I></combined>'
    $null = Assert-SharedIndex -Path $validXmlPath -ExpectedStatus 'valid' -ExpectedIssueCount 0

    $referenceBeforeDefinitionPath = Join-Path $fixtureRoot 'reference-before-definition.xml'
    Write-Utf8File $referenceBeforeDefinitionPath '<combined><I><r x="a"/></I><g><E x="a"/></g></combined>'
    $null = Assert-SharedIndex -Path $referenceBeforeDefinitionPath -ExpectedStatus 'valid' -ExpectedIssueCount 0

    $missingDefinitionPath = Join-Path $fixtureRoot 'missing-definition-index.xml'
    Write-Utf8File $missingDefinitionPath '<combined><g><E/><T x="b"/></g><I><r x="b"/><r x="a"/></I></combined>'
    $missingDefinition = Assert-SharedIndex -Path $missingDefinitionPath -ExpectedStatus 'invalid' -ExpectedIssueCount 2 -ExpectedExitCode 1
    if ($missingDefinition.sharedIndexValidation.missingDefinitionIndexCount -ne 1 -or
        $missingDefinition.sharedIndexValidation.unresolvedReferenceCount -ne 1) {
        throw 'Missing definition x was not reported with the unresolved use.'
    }

    $duplicateDefinitionPath = Join-Path $fixtureRoot 'duplicate-definition-index.xml'
    Write-Utf8File $duplicateDefinitionPath '<combined><g><E x="a"/><T x="a"/></g><I><r x="a"/></I></combined>'
    $duplicateDefinition = Assert-SharedIndex -Path $duplicateDefinitionPath -ExpectedStatus 'invalid' -ExpectedIssueCount 2 -ExpectedExitCode 1
    if ($duplicateDefinition.sharedIndexValidation.duplicateDefinitionIndexCount -ne 1 -or
        $duplicateDefinition.sharedIndexValidation.ambiguousReferenceCount -ne 1) {
        throw 'Duplicate definition x was not reported as ambiguous.'
    }

    $missingReferencePath = Join-Path $fixtureRoot 'missing-reference-index.xml'
    Write-Utf8File $missingReferencePath '<combined><g><E x="a"/></g><I><r/></I></combined>'
    $missingReference = Assert-SharedIndex -Path $missingReferencePath -ExpectedStatus 'invalid' -ExpectedIssueCount 1 -ExpectedExitCode 1
    if ($missingReference.sharedIndexValidation.missingReferenceIndexCount -ne 1) {
        throw 'A missing r x was not reported.'
    }

    $unresolvedReferencePath = Join-Path $fixtureRoot 'unresolved-reference.xml'
    Write-Utf8File $unresolvedReferencePath '<combined><g><E x="a"/></g><I><r x="missing"/></I></combined>'
    $unresolvedReference = Assert-SharedIndex -Path $unresolvedReferencePath -ExpectedStatus 'invalid' -ExpectedIssueCount 1 -ExpectedExitCode 1
    if ($unresolvedReference.sharedIndexValidation.unresolvedReferenceCount -ne 1) {
        throw 'An unresolved r x was not reported.'
    }

    $unsupportedLayoutPath = Join-Path $fixtureRoot 'multiple-tables.xml'
    Write-Utf8File $unsupportedLayoutPath '<combined><g><E x="a"/></g><g><T x="b"/></g><I><r x="a"/></I></combined>'
    $unsupportedLayout = Assert-SharedIndex -Path $unsupportedLayoutPath -ExpectedStatus 'unsupported' -ExpectedIssueCount 1 -ExpectedExitCode 1
    if ($unsupportedLayout.sharedIndexValidation.unsupportedReasonCount -ne 1) {
        throw 'Multiple shared-table containers were silently merged.'
    }

    $buildPath = Join-Path $fixtureRoot 'invalid-build.package'
    $buildFailure = Invoke-S4mtJson -Arguments @(
        'tuning', 'build', '--tgi', $combinedTgi, '--xml', $unresolvedReferencePath, '--output', $buildPath) -ExpectedExitCode 2
    if ($buildFailure.error.code -ne 'invalid_input' -or (Test-Path -LiteralPath $buildPath)) {
        throw "Tuning build did not refuse invalid shared indexes before publishing output: $($buildFailure | ConvertTo-Json -Compress -Depth 5); outputExists=$(Test-Path -LiteralPath $buildPath)"
    }

    $importPath = Join-Path $fixtureRoot 'invalid-import.package'
    $importFailure = Invoke-S4mtJson -Arguments @(
        'tuning', 'import', '--package', $fullBuildPackage, '--tgi', $combinedTgi,
        '--xml', $unresolvedReferencePath, '--output', $importPath) -ExpectedExitCode 2
    if ($importFailure.error.code -ne 'invalid_input' -or (Test-Path -LiteralPath $importPath)) {
        throw 'Tuning import did not refuse invalid shared indexes before publishing output.'
    }

    $null = Invoke-S4mtJson -Arguments @('project', 'init', '--name', 'CombinedIndexMod', '--path', $projectRoot)
    New-Item -ItemType Directory -Force -Path $resourceRoot | Out-Null
    $projectXmlPath = Join-Path $resourceRoot 'combined.xml'
    Copy-Item -LiteralPath $unresolvedReferencePath -Destination $projectXmlPath
    $projectFile = [ordered]@{
        format = 1
        name = 'CombinedIndexMod'
        packageManifest = 'package.json'
        scriptArchives = @()
    }
    $packageManifest = [ordered]@{
        format = 1
        resources = @([ordered]@{
            type = '0x62E94D38'
            group = '0x00000000'
            instance = '0xF1352E740C4710BA'
            source = 'resources/combined.xml'
            encoding = 'utf8'
        })
    }
    Write-Utf8File (Join-Path $projectRoot 's4mt.json') (ConvertTo-Json -InputObject $projectFile -Depth 5)
    Write-Utf8File (Join-Path $projectRoot 'package.json') (ConvertTo-Json -InputObject $packageManifest -Depth 5)

    $projectValidation = Invoke-S4mtJson -Arguments @('project', 'validate', '--project', $projectRoot) -ExpectedExitCode 1
    $projectDiagnostic = $projectValidation.diagnostics |
        Where-Object { $_.code -eq 'invalid_combined_tuning_shared_index' -and $_.path -eq $projectXmlPath }
    if ($projectValidation.valid -ne $false -or $null -eq $projectDiagnostic -or
        $projectDiagnostic.message -notmatch [regex]::Escape($combinedTgi)) {
        throw "Project validation lost the shared-index error, TGI, or source path: $($projectValidation | ConvertTo-Json -Compress -Depth 6)"
    }

    $projectBuild = Invoke-S4mtJson -Arguments @(
        'project', 'build', '--project', $projectRoot, '--output-root', $outputRoot) -ExpectedExitCode 1
    if ($projectBuild.valid -ne $false -or (Test-Path -LiteralPath $outputRoot)) {
        throw 'Project build published output despite invalid CombinedTuning shared indexes.'
    }

    $packageManifest.resources[0].encoding = 'bytes'
    $binaryPayloadPath = Join-Path $resourceRoot 'combined-data.bin'
    $binaryExport = Invoke-S4mtJson -Arguments @(
        'resource', 'export', '--package', $deltaBuildPackage, '--tgi', $combinedTgi, '--output', $binaryPayloadPath)
    if ($binaryExport.payloadBytes -le 0) {
        throw 'Could not export the binary CombinedTuning boundary fixture.'
    }
    $packageManifest.resources[0].source = 'resources/combined-data.bin'
    Write-Utf8File (Join-Path $projectRoot 'package.json') (ConvertTo-Json -InputObject $packageManifest -Depth 5)
    $binaryProjectValidation = Invoke-S4mtJson -Arguments @('project', 'validate', '--project', $projectRoot)
    if ($binaryProjectValidation.valid -ne $true -or
        ($binaryProjectValidation.diagnostics | Where-Object { $_.code -like '*combined_tuning_shared_index*' })) {
        throw 'Project validation tried to interpret opaque binary CombinedTuning bytes.'
    }

    [pscustomobject]@{
        result = 'verified'
        realFixtureTgi = $combinedTgi
        realXmlGroupCount = $realValidation.sharedIndexValidation.sharedTableCount
        realXmlDefinitionCount = $realValidation.sharedIndexValidation.definitionCount
        realXmlReferenceCount = $realValidation.sharedIndexValidation.referenceCount
        malformedCases = 5
        multipleTableLayout = $unsupportedLayout.sharedIndexValidation.status
        directBuildRejected = $buildFailure.error.code -eq 'invalid_input'
        directImportRejected = $importFailure.error.code -eq 'invalid_input'
        projectDiagnosticPath = $projectDiagnostic.path
        projectBuildBlocked = $projectBuild.valid -eq $false
        binaryBytesOpaque = $binaryProjectValidation.valid -eq $true
    } | ConvertTo-Json -Compress -Depth 4
}
finally {
    $resolvedFixtureRoot = [System.IO.Path]::GetFullPath($fixtureRoot)
    $resolvedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedFixtureRoot.StartsWith($resolvedTempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete fixture outside the system temporary directory: $resolvedFixtureRoot"
    }
    if (Test-Path -LiteralPath $resolvedFixtureRoot) {
        Remove-Item -LiteralPath $resolvedFixtureRoot -Recurse -Force
    }
}
