[CmdletBinding(DefaultParameterSetName = 'Windows')]
param(
    [Parameter(Mandatory = $true)]
    [string]$Package,
    [string]$ExpectedSha256,
    [Parameter(Mandatory = $true, ParameterSetName = 'StageOnly')]
    [switch]$StageOnly,
    [Parameter(Mandatory = $true, ParameterSetName = 'StageOnly')]
    [string]$SourceProfile,
    [Parameter(ParameterSetName = 'StageOnly')]
    [string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
$runningOnWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)
$pathComparison = if ($runningOnWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }

function Normalize-DirectoryPath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    $rootPath = [IO.Path]::GetPathRoot($fullPath)
    $trimmedPath = $fullPath.TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
    if ($trimmedPath.Length -lt $rootPath.Length) { return $rootPath }
    return $trimmedPath
}

function Get-PathComponents([string]$Path) {
    return @($Path -split '[\\/]' | Where-Object { $_.Length -gt 0 })
}

function Assert-NoReparsePointsInAncestry([string]$Path, [string]$StopAt) {
    $currentPath = [IO.Path]::GetFullPath($Path)
    $stopPath = if ($StopAt) { Normalize-DirectoryPath $StopAt } else { $null }
    while ($true) {
        if (Test-Path -LiteralPath $currentPath) {
            $attributes = [IO.File]::GetAttributes($currentPath)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing a path with a reparse point in its ancestry: $currentPath"
            }
        }
        if ($stopPath -and [string]::Equals((Normalize-DirectoryPath $currentPath), $stopPath, $pathComparison)) { break }
        $parent = [IO.Directory]::GetParent($currentPath)
        if ($null -eq $parent) { break }
        $currentPath = $parent.FullName
    }
}

function Get-PackageEvidence([string]$Path) {
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    $length = [long](Get-Item -LiteralPath $Path -Force).Length
    return [pscustomobject]@{ Sha256 = $hash; Length = $length }
}

function New-OwnedPackage([string]$Path, [string]$ExpectedHash) {
    $sourcePath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf) -or -not [IO.File]::Exists($sourcePath)) {
        throw "The runtime fixture must be a regular .package file: $sourcePath"
    }
    $leaf = [IO.Path]::GetFileName($sourcePath)
    if (-not [string]::Equals([IO.Path]::GetExtension($leaf), '.package', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The runtime fixture must be a .package file.'
    }

    $buildDirectory = [IO.Path]::GetDirectoryName($sourcePath)
    $builtDirectory = [IO.Path]::GetDirectoryName($buildDirectory)
    $artifactRoot = [IO.Path]::GetDirectoryName($builtDirectory)
    if (-not $artifactRoot -or -not [IO.Directory]::Exists($artifactRoot)) {
        throw "The runtime fixture must be inside an owned built/<build>/<leaf>.package tree: $sourcePath"
    }
    $artifactRoot = Normalize-DirectoryPath $artifactRoot
    $relativePackage = [IO.Path]::GetRelativePath($artifactRoot, $sourcePath)
    $packageParts = Get-PathComponents $relativePackage
    if ([IO.Path]::IsPathRooted($relativePackage) -or $packageParts.Count -ne 3 -or
        -not [string]::Equals($packageParts[0], 'built', $pathComparison) -or
        [string]::IsNullOrWhiteSpace($packageParts[1]) -or $packageParts[1] -in @('.', '..') -or
        -not [string]::Equals($packageParts[2], $leaf, $pathComparison)) {
        throw "The runtime fixture must be at built/<build>/<leaf>.package below its owned root: $sourcePath"
    }

    $tempRoot = Normalize-DirectoryPath ([IO.Path]::GetTempPath())
    $artifactParent = Normalize-DirectoryPath ([IO.Path]::GetDirectoryName($artifactRoot))
    $artifactLeaf = [IO.Path]::GetFileName($artifactRoot)
    if (-not [string]::Equals($artifactParent, $tempRoot, $pathComparison) -or
        -not $artifactLeaf.StartsWith('s4mt-agent-authoring-', $pathComparison)) {
        throw "Refusing to install a package outside a direct owned temp root: $sourcePath"
    }

    Assert-NoReparsePointsInAncestry $sourcePath $null
    $evidence = Get-PackageEvidence $sourcePath
    if ($ExpectedHash) {
        if ($ExpectedHash -notmatch '^[0-9A-Fa-f]{64}$') { throw 'ExpectedSha256 must contain exactly 64 hexadecimal characters.' }
        if (-not [string]::Equals($evidence.Sha256, $ExpectedHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The runtime package SHA-256 does not match ExpectedSha256: $sourcePath"
        }
    }

    return [pscustomobject]@{
        SourcePath = $sourcePath
        ArtifactRoot = $artifactRoot
        Leaf = $leaf
        SourceSha256 = $evidence.Sha256
        SourceLength = $evidence.Length
    }
}

function Assert-PathInside([string]$BasePath, [string]$Path, [string]$Label) {
    $base = Normalize-DirectoryPath $BasePath
    $fullPath = [IO.Path]::GetFullPath($Path)
    $relative = [IO.Path]::GetRelativePath($base, $fullPath)
    $components = Get-PathComponents $relative
    if ([IO.Path]::IsPathRooted($relative) -or
        ($components.Count -gt 0 -and $components[0] -eq '..')) {
        throw "$Label must be within the owned artifact root: $fullPath"
    }
    return $fullPath
}

function Copy-RuntimeProfile([pscustomobject]$OwnedPackage, [string]$SourceProfilePath, [string]$DestinationProfilePath) {
    if (-not [IO.Directory]::Exists($SourceProfilePath)) { throw "The source profile does not exist: $SourceProfilePath" }
    $optionsPath = Join-Path $SourceProfilePath 'Options.ini'
    $resourceConfigPath = Join-Path $SourceProfilePath 'Mods/Resource.cfg'
    foreach ($sourceFile in @($optionsPath, $resourceConfigPath)) {
        if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf) -or -not [IO.File]::Exists($sourceFile)) {
            throw "The source profile lacks a regular required file: $sourceFile"
        }
        Assert-NoReparsePointsInAncestry $sourceFile $SourceProfilePath
    }

    $modsPath = Join-Path $DestinationProfilePath 'Mods'
    $destinationOptionsPath = Join-Path $DestinationProfilePath 'Options.ini'
    $destinationResourceConfigPath = Join-Path $modsPath 'Resource.cfg'
    $optionsBeforeCopy = Get-PackageEvidence $optionsPath
    $resourceConfigBeforeCopy = Get-PackageEvidence $resourceConfigPath
    [IO.Directory]::CreateDirectory($modsPath) | Out-Null
    Copy-Item -LiteralPath $optionsPath -Destination $destinationOptionsPath
    Copy-Item -LiteralPath $resourceConfigPath -Destination $destinationResourceConfigPath
    $destinationPackage = Join-Path $modsPath $OwnedPackage.Leaf
    Copy-Item -LiteralPath $OwnedPackage.SourcePath -Destination $destinationPackage

    $optionsAfterCopy = Get-PackageEvidence $optionsPath
    $optionsStagedEvidence = Get-PackageEvidence $destinationOptionsPath
    $resourceConfigAfterCopy = Get-PackageEvidence $resourceConfigPath
    $resourceConfigStagedEvidence = Get-PackageEvidence $destinationResourceConfigPath
    $sourceAfterCopy = Get-PackageEvidence $OwnedPackage.SourcePath
    $stagedEvidence = Get-PackageEvidence $destinationPackage
    if ($optionsAfterCopy.Length -ne $optionsBeforeCopy.Length -or
        -not [string]::Equals($optionsAfterCopy.Sha256, $optionsBeforeCopy.Sha256, [StringComparison]::Ordinal) -or
        $optionsStagedEvidence.Length -ne $optionsBeforeCopy.Length -or
        -not [string]::Equals($optionsStagedEvidence.Sha256, $optionsBeforeCopy.Sha256, [StringComparison]::Ordinal) -or
        $resourceConfigAfterCopy.Length -ne $resourceConfigBeforeCopy.Length -or
        -not [string]::Equals($resourceConfigAfterCopy.Sha256, $resourceConfigBeforeCopy.Sha256, [StringComparison]::Ordinal) -or
        $resourceConfigStagedEvidence.Length -ne $resourceConfigBeforeCopy.Length -or
        -not [string]::Equals($resourceConfigStagedEvidence.Sha256, $resourceConfigBeforeCopy.Sha256, [StringComparison]::Ordinal) -or
        $sourceAfterCopy.Length -ne $OwnedPackage.SourceLength -or
        -not [string]::Equals($sourceAfterCopy.Sha256, $OwnedPackage.SourceSha256, [StringComparison]::Ordinal) -or
        $stagedEvidence.Length -ne $OwnedPackage.SourceLength -or
        -not [string]::Equals($stagedEvidence.Sha256, $OwnedPackage.SourceSha256, [StringComparison]::Ordinal)) {
        throw "The staged profile does not match its source files: $DestinationProfilePath"
    }

    return [pscustomobject]@{
        OwnedPackage = $OwnedPackage
        DestinationPath = $destinationPackage
        StagedSha256 = $stagedEvidence.Sha256
        StagedLength = $stagedEvidence.Length
        OptionsSha256 = $optionsStagedEvidence.Sha256
        OptionsLength = $optionsStagedEvidence.Length
        ResourceConfigSha256 = $resourceConfigStagedEvidence.Sha256
        ResourceConfigLength = $resourceConfigStagedEvidence.Length
    }
}

function Invoke-StageOnly([pscustomobject]$OwnedPackage, [string]$SourceProfilePath, [string]$EvidenceDirectoryPath) {
    $sourceProfileFullPath = Assert-PathInside $OwnedPackage.ArtifactRoot $SourceProfilePath 'SourceProfile'
    if (-not [IO.Directory]::Exists($sourceProfileFullPath)) { throw "SourceProfile does not exist: $sourceProfileFullPath" }
    Assert-NoReparsePointsInAncestry $sourceProfileFullPath $OwnedPackage.ArtifactRoot
    if ($EvidenceDirectoryPath) {
        $EvidenceDirectoryPath = Assert-PathInside $OwnedPackage.ArtifactRoot $EvidenceDirectoryPath 'EvidenceDirectory'
        if (Test-Path -LiteralPath $EvidenceDirectoryPath) { throw "EvidenceDirectory already exists: $EvidenceDirectoryPath" }
        Assert-NoReparsePointsInAncestry $EvidenceDirectoryPath $OwnedPackage.ArtifactRoot
    }
    $profilePath = Join-Path ([IO.Path]::GetTempPath()) "s4mt-runtime-stage-profile-$([Guid]::NewGuid().ToString('N'))"
    $profilePath = [IO.Path]::GetFullPath($profilePath)
    if (Test-Path -LiteralPath $profilePath) { throw 'The unique staging profile path already exists.' }

    $created = $false
    $result = $null
    try {
        New-Item -ItemType Directory -Path $profilePath -ErrorAction Stop | Out-Null
        $created = $true
        $stagedPackage = Copy-RuntimeProfile $OwnedPackage $sourceProfileFullPath $profilePath
        if ($EvidenceDirectoryPath) {
            $evidenceModsPath = Join-Path $EvidenceDirectoryPath 'Mods'
            [IO.Directory]::CreateDirectory($evidenceModsPath) | Out-Null
            Copy-Item -LiteralPath (Join-Path $profilePath 'Options.ini') -Destination (Join-Path $EvidenceDirectoryPath 'Options.ini')
            Copy-Item -LiteralPath (Join-Path $profilePath 'Mods/Resource.cfg') -Destination (Join-Path $evidenceModsPath 'Resource.cfg')
            Copy-Item -LiteralPath $stagedPackage.DestinationPath -Destination (Join-Path $evidenceModsPath $OwnedPackage.Leaf)
        }
        $result = [ordered]@{
            result = 'staged-and-checked'
            packageName = $OwnedPackage.Leaf
            sourceSha256 = $OwnedPackage.SourceSha256
            stagedSha256 = $stagedPackage.StagedSha256
            packageHash = $stagedPackage.StagedSha256
            profileSwitched = $false
            runtimeBehaviorTested = $false
        }
    } finally {
        if ($created -and [IO.Directory]::Exists($profilePath)) {
            $profileAttributes = [IO.File]::GetAttributes($profilePath)
            if (($profileAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'The temporary staging profile became a reparse point before cleanup.'
            }
            [IO.Directory]::Delete($profilePath, $true)
        }
    }

    if ([IO.Directory]::Exists($profilePath)) { throw 'The temporary staging profile was not removed.' }
    $result.profileRemoved = $true
    return $result
}

if ($PSCmdlet.ParameterSetName -eq 'StageOnly') {
    $ownedPackage = New-OwnedPackage $Package $ExpectedSha256
    $stageResult = Invoke-StageOnly $ownedPackage $SourceProfile $EvidenceDirectory
    $stageResult | ConvertTo-Json -Depth 4
    return
}

if (-not $runningOnWindows) { throw 'Windows profile preparation requires Windows.' }

$ownedPackage = New-OwnedPackage $Package $ExpectedSha256
$tempRoot = Normalize-DirectoryPath ([IO.Path]::GetTempPath())
$documents = [Environment]::GetFolderPath('MyDocuments')
$parent = Join-Path $documents 'Electronic Arts'
$userPath = Join-Path $parent 'The Sims 4'
$gameExe = 'C:\Program Files\EA Games\The Sims 4\Game\Bin\TS4_x64.exe'
$resourceConfig = Join-Path $userPath 'Mods\Resource.cfg'
$optionsPath = Join-Path $userPath 'Options.ini'
$statePath = Join-Path $tempRoot 's4mt-runtime-smoke-state.json'
if (Test-Path -LiteralPath $statePath) { throw "A runtime profile is already prepared; restore it first: $statePath" }
if (-not (Test-Path -LiteralPath $gameExe -PathType Leaf)) { throw "Game executable not found: $gameExe" }
if (-not (Test-Path -LiteralPath $resourceConfig -PathType Leaf) -or -not (Test-Path -LiteralPath $optionsPath -PathType Leaf)) {
    throw 'The current profile lacks Options.ini or Mods\Resource.cfg; cannot preserve its mod-loading setup.'
}
if (Get-Process -Name TS4_x64 -ErrorAction SilentlyContinue) { throw 'Close Sims 4 before switching its user-data path.' }
$userInfo = [IO.DirectoryInfo]::new($userPath)
if (($userInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The current Sims 4 profile is already a link; refusing to replace it.' }
$backupPath = Join-Path $parent ('.s4mt-original-' + [guid]::NewGuid().ToString('N'))
$profilePath = Join-Path $tempRoot ('s4mt-runtime-profile-' + [guid]::NewGuid().ToString('N'))
$profileCreated = $false
$state = [ordered]@{
    userPath = $userPath
    backupPath = $backupPath
    profilePath = $profilePath
    packagePath = $ownedPackage.SourcePath
    artifactRoot = $ownedPackage.ArtifactRoot
    packageName = $ownedPackage.Leaf
    sourceSha256 = $ownedPackage.SourceSha256
    statePath = $statePath
    gameExe = $gameExe
}

try {
    New-Item -ItemType Directory -Path $profilePath -ErrorAction Stop | Out-Null
    $profileCreated = $true
    $stagedPackage = Copy-RuntimeProfile $ownedPackage $userPath $profilePath
    [IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    [IO.Directory]::Move($userPath, $backupPath)
    New-Item -ItemType Junction -Path $userPath -Target $profilePath | Out-Null
    $resolved = ([IO.DirectoryInfo]::new($userPath)).ResolveLinkTarget($true)
    if ($null -eq $resolved -or [IO.Path]::GetFullPath($resolved.FullName) -ne [IO.Path]::GetFullPath($profilePath)) {
        throw 'The profile junction did not resolve to the isolated directory.'
    }
} catch {
    if (Test-Path -LiteralPath $userPath) {
        $link = [IO.DirectoryInfo]::new($userPath)
        $target = $link.ResolveLinkTarget($true)
        if ($null -ne $target -and [IO.Path]::GetFullPath($target.FullName) -eq [IO.Path]::GetFullPath($profilePath)) {
            [IO.Directory]::Delete($userPath)
        } elseif (-not (Test-Path -LiteralPath $backupPath)) {
            if ($profileCreated -and (Test-Path -LiteralPath $profilePath)) { Remove-Item -LiteralPath $profilePath -Recurse -Force }
            Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
            throw
        } else {
            throw 'Setup failed and an unexpected path now occupies the Sims 4 profile location; original backup preserved.'
        }
    }
    if (Test-Path -LiteralPath $backupPath) { [IO.Directory]::Move($backupPath, $userPath) }
    if ($profileCreated -and (Test-Path -LiteralPath $profilePath)) { Remove-Item -LiteralPath $profilePath -Recurse -Force }
    Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
    throw
}

[pscustomobject]@{
    result = 'ready'
    profile = $profilePath
    junction = $userPath
    backup = $backupPath
    packageName = $ownedPackage.Leaf
    sourceSha256 = $ownedPackage.SourceSha256
    stagedSha256 = $stagedPackage.StagedSha256
    packageHash = $stagedPackage.StagedSha256
    runtimeBehaviorTested = $false
} | ConvertTo-Json -Depth 4
