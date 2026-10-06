$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$exampleRoot = Join-Path $repoRoot 'examples/StressedMoodGreen'
$prepareScript = Join-Path $PSScriptRoot 'prepare-runtime-profile.ps1'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
$artifactRoot = Join-Path $tempRoot "s4mt-agent-authoring-$([Guid]::NewGuid().ToString('N'))"
$artifactRoot = [IO.Path]::GetFullPath($artifactRoot)
$outputRoot = Join-Path $artifactRoot 'built'
$sourceProfile = Join-Path $artifactRoot 'source-profile'
$sourceProfileMods = Join-Path $sourceProfile 'Mods'
$stageEvidenceDirectory = Join-Path $artifactRoot 'stage-evidence'
$artifactRootParent = [IO.Path]::GetFullPath([IO.Path]::GetDirectoryName($artifactRoot)).TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
$expectedTempRoot = $tempRoot
if (-not [string]::Equals($artifactRootParent, $expectedTempRoot, [StringComparison]::Ordinal) -or
    -not [IO.Path]::GetFileName($artifactRoot).StartsWith('s4mt-agent-authoring-', [StringComparison]::Ordinal)) {
    throw "Verifier artifact root is not a direct child of system temp: $artifactRoot"
}
if (Test-Path -LiteralPath $artifactRoot) { throw 'The unique verifier artifact root already exists.' }
$artifactRootCreated = $false

function Get-StageProfilePaths([string]$Directory) {
    if (-not [IO.Directory]::Exists($Directory)) { throw "System temp directory is unavailable: $Directory" }
    try {
        return @(
            Get-ChildItem -LiteralPath $Directory -Directory -Filter 's4mt-runtime-stage-profile-*' -ErrorAction Stop |
                ForEach-Object { [IO.Path]::GetFullPath($_.FullName) }
        )
    } catch {
        throw "Could not inspect temporary stage profiles in $Directory`: $($_.Exception.Message)"
    }
}

function Get-FileEvidence([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Expected file was not produced: $Path" }
    return [pscustomobject]@{
        Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
        Length = [long](Get-Item -LiteralPath $Path -Force).Length
    }
}

function Invoke-Process([string]$Command, [string[]]$Arguments) {
    $lines = @(& $Command @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output = ($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    if ($exitCode -ne 0) { throw "Command failed with exit code ${exitCode}: $Command $($Arguments -join ' ')`n$output" }
    return [pscustomobject]@{ Output = $output; Lines = $lines }
}

function Invoke-ExpectedFailure([string]$Command, [string[]]$Arguments, [string]$ExpectedText) {
    $lines = @(& $Command @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output = ($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    if ($exitCode -eq 0 -or -not $output.Contains($ExpectedText)) {
        throw "Command did not fail with the expected message '$ExpectedText': $Command $($Arguments -join ' ')`n$output"
    }
    return $output
}

try {
    New-Item -ItemType Directory -Path $artifactRoot -ErrorAction Stop | Out-Null
    $artifactRootCreated = $true
    [IO.Directory]::CreateDirectory($sourceProfileMods) | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $sourceProfile 'Options.ini'),
        "fullscreen = 0`n",
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $sourceProfileMods 'Resource.cfg'),
        "Priority 500`nPackedFile *.package`n",
        [Text.UTF8Encoding]::new($false))

    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $pwsh = (Get-Command pwsh -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $build = Invoke-Process $dotnet @(
        'run',
        '--project', (Join-Path $repoRoot 'src/S4MT.Cli'),
        '--',
        'project', 'build',
        '--project', $exampleRoot,
        '--output-root', $outputRoot
    )
    $buildJsonLine = @($build.Lines | ForEach-Object { $_.ToString() } |
        Where-Object { $_ -match '"command"\s*:\s*"project\.build"' } | Select-Object -Last 1)
    if ($buildJsonLine.Count -ne 1) { throw "CLI output did not contain one project.build result:`n$($build.Output)" }
    $buildResult = $buildJsonLine[0] | ConvertFrom-Json
    if (-not $buildResult.valid -or $buildResult.resourceCount -lt 1 -or
        -not (Test-Path -LiteralPath $buildResult.package -PathType Leaf)) {
        throw "The pinned example did not build a package: $($buildResult | ConvertTo-Json -Compress)"
    }

    $packagePath = [IO.Path]::GetFullPath([string]$buildResult.package)
    $relativePackage = [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($outputRoot), $packagePath)
    $packageParts = @($relativePackage -split '[\\/]' | Where-Object { $_.Length -gt 0 })
    if ([IO.Path]::IsPathRooted($relativePackage) -or $packageParts.Count -ne 2 -or
        -not [string]::Equals($packageParts[1], 'StressedMoodGreen.package', [StringComparison]::Ordinal)) {
        throw "The CLI package was not written beneath built/<build>/<leaf>.package: $packagePath"
    }

    $expectedHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToUpperInvariant()
    $stageProfilesBefore = Get-StageProfilePaths $tempRoot
    $stage = Invoke-Process $pwsh @(
        '-NoLogo',
        '-NoProfile',
        '-File', $prepareScript,
        '-StageOnly',
        '-Package', $packagePath,
        '-SourceProfile', $sourceProfile,
        '-ExpectedSha256', $expectedHash,
        '-EvidenceDirectory', $stageEvidenceDirectory
    )
    $stageResult = $stage.Output | ConvertFrom-Json
    if ($stageResult.result -ne 'staged-and-checked' -or
        $stageResult.packageName -ne 'StressedMoodGreen.package' -or
        $stageResult.sourceSha256 -ne $expectedHash -or
        $stageResult.stagedSha256 -ne $expectedHash -or
        $stageResult.packageHash -ne $expectedHash -or
        $stageResult.profileSwitched -ne $false -or
        $stageResult.runtimeBehaviorTested -ne $false -or
        $stageResult.profileRemoved -ne $true) {
        throw "StageOnly returned an unexpected result: $($stageResult | ConvertTo-Json -Compress)"
    }
    $forbiddenProperties = @('profile', 'profilePath', 'sourceProfile', 'junction', 'backup')
    if (@($stageResult.PSObject.Properties.Name | Where-Object { $_ -in $forbiddenProperties }).Count -gt 0 -or
        $stage.Output.Contains($sourceProfile) -or
        $stage.Output.Contains($stageEvidenceDirectory)) {
        throw 'StageOnly returned a profile path.'
    }
    $stageProfilesAfter = Get-StageProfilePaths $tempRoot
    $remainingProfiles = @($stageProfilesAfter | Where-Object { $_ -notin $stageProfilesBefore })
    if ($remainingProfiles.Count -gt 0) { throw 'StageOnly left a temporary profile in system temp.' }

    $stagedFiles = @(
        [pscustomobject]@{ Name = 'package'; Source = $packagePath; Staged = (Join-Path $stageEvidenceDirectory 'Mods/StressedMoodGreen.package') },
        [pscustomobject]@{ Name = 'Options.ini'; Source = (Join-Path $sourceProfile 'Options.ini'); Staged = (Join-Path $stageEvidenceDirectory 'Options.ini') },
        [pscustomobject]@{ Name = 'Resource.cfg'; Source = (Join-Path $sourceProfileMods 'Resource.cfg'); Staged = (Join-Path $stageEvidenceDirectory 'Mods/Resource.cfg') }
    )
    foreach ($file in $stagedFiles) {
        $sourceEvidence = Get-FileEvidence $file.Source
        $stagedEvidence = Get-FileEvidence $file.Staged
        if ($stagedEvidence.Length -ne $sourceEvidence.Length -or
            -not [string]::Equals($stagedEvidence.Sha256, $sourceEvidence.Sha256, [StringComparison]::Ordinal)) {
            throw "The observed staged $($file.Name) does not match its source file."
        }
    }

    $wrongHashProfilesBefore = Get-StageProfilePaths $tempRoot
    $null = Invoke-ExpectedFailure $pwsh @(
        '-NoLogo', '-NoProfile', '-File', $prepareScript,
        '-StageOnly', '-Package', $packagePath,
        '-SourceProfile', $sourceProfile,
        '-ExpectedSha256', ('0' * 64)
    ) 'does not match ExpectedSha256'
    $wrongHashProfilesAfter = Get-StageProfilePaths $tempRoot
    if (@($wrongHashProfilesAfter | Where-Object { $_ -notin $wrongHashProfilesBefore }).Count -gt 0) {
        throw 'A package rejected for the wrong expected hash created a temporary profile.'
    }

    $wrongLayoutPackage = Join-Path $artifactRoot 'wrong-location.package'
    [IO.File]::Copy($packagePath, $wrongLayoutPackage)
    $null = Invoke-ExpectedFailure $pwsh @(
        '-NoLogo', '-NoProfile', '-File', $prepareScript,
        '-StageOnly', '-Package', $wrongLayoutPackage,
        '-SourceProfile', $sourceProfile
    ) 'must be at built/'

    $incompleteProfile = Join-Path $artifactRoot 'incomplete-profile'
    [IO.Directory]::CreateDirectory($incompleteProfile) | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $incompleteProfile 'Options.ini'),
        "fullscreen = 0`n",
        [Text.UTF8Encoding]::new($false))
    $failedCopyProfilesBefore = Get-StageProfilePaths $tempRoot
    $null = Invoke-ExpectedFailure $pwsh @(
        '-NoLogo', '-NoProfile', '-File', $prepareScript,
        '-StageOnly', '-Package', $packagePath,
        '-SourceProfile', $incompleteProfile
    ) 'lacks a regular required file'
    $failedCopyProfilesAfter = Get-StageProfilePaths $tempRoot
    if (@($failedCopyProfilesAfter | Where-Object { $_ -notin $failedCopyProfilesBefore }).Count -gt 0) {
        throw 'A failed profile copy left a temporary profile in system temp.'
    }

    $linkedModsCheck = 'not-run-on-Windows'
    if ([IO.Path]::DirectorySeparatorChar -eq '/') {
        $linkedModsProfile = Join-Path $artifactRoot 'linked-mods-profile'
        $linkedModsSource = Join-Path $artifactRoot 'linked-mods-source'
        [IO.Directory]::CreateDirectory($linkedModsProfile) | Out-Null
        [IO.Directory]::CreateDirectory($linkedModsSource) | Out-Null
        [IO.File]::WriteAllText((Join-Path $linkedModsProfile 'Options.ini'), "fullscreen = 0`n", [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $linkedModsSource 'Resource.cfg'), "Priority 500`n", [Text.UTF8Encoding]::new($false))
        [IO.Directory]::CreateSymbolicLink((Join-Path $linkedModsProfile 'Mods'), $linkedModsSource) | Out-Null
        $linkedModsProfilesBefore = Get-StageProfilePaths $tempRoot
        $null = Invoke-ExpectedFailure $pwsh @(
            '-NoLogo', '-NoProfile', '-File', $prepareScript,
            '-StageOnly', '-Package', $packagePath,
            '-SourceProfile', $linkedModsProfile
        ) 'reparse point in its ancestry'
        $linkedModsProfilesAfter = Get-StageProfilePaths $tempRoot
        if (@($linkedModsProfilesAfter | Where-Object { $_ -notin $linkedModsProfilesBefore }).Count -gt 0) {
            throw 'A profile with a linked Mods directory left a temporary profile.'
        }
        $linkedModsCheck = 'rejected'
    }

    [pscustomobject]@{
        result = 'verified'
        stagingResult = $stageResult.result
        packageName = $stageResult.packageName
        sourceSha256 = $stageResult.sourceSha256
        stagedSha256 = $stageResult.stagedSha256
        profileSwitched = $stageResult.profileSwitched
        runtimeBehaviorTested = $stageResult.runtimeBehaviorTested
        temporaryProfileRemoved = $stageResult.profileRemoved
        observedPackageMatches = $true
        observedOptionsIniMatches = $true
        observedResourceConfigMatches = $true
        wrongHashRejected = $true
        wrongPackageLayoutRejected = $true
        missingConfigRejectedAndStageRemoved = $true
        linkedModsCheck = $linkedModsCheck
    } | ConvertTo-Json -Depth 4
} finally {
    if ($artifactRootCreated -and [IO.Directory]::Exists($artifactRoot)) {
        $attributes = [IO.File]::GetAttributes($artifactRoot)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Verifier artifact root became a reparse point before cleanup.'
        }
        [IO.Directory]::Delete($artifactRoot, $true)
    }
}
