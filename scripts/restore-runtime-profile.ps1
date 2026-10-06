$ErrorActionPreference = 'Stop'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$statePath = Join-Path ([IO.Path]::GetTempPath()) 's4mt-runtime-smoke-state.json'
if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw 'No S4MT runtime profile state was found.' }
if (Get-Process -Name TS4_x64 -ErrorAction SilentlyContinue) { throw 'Close Sims 4 before restoring its original profile path.' }
$state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
$userPath = [IO.Path]::GetFullPath($state.userPath)
$backupPath = [IO.Path]::GetFullPath($state.backupPath)
$profilePath = [IO.Path]::GetFullPath($state.profilePath)
$packagePath = [IO.Path]::GetFullPath($state.packagePath)
$artifactRoot = [IO.Path]::GetFullPath($state.artifactRoot)
$documentsParent = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Electronic Arts'))
if (-not $userPath.StartsWith($documentsParent, [StringComparison]::OrdinalIgnoreCase) -or
    -not $backupPath.StartsWith($documentsParent, [StringComparison]::OrdinalIgnoreCase) -or
    -not $profilePath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not $packagePath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not $artifactRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($backupPath) -notlike '.s4mt-original-*' -or
    [IO.Path]::GetFileName($profilePath) -notlike 's4mt-runtime-profile-*' -or
    [IO.Path]::GetFileName($artifactRoot) -notlike 's4mt-agent-authoring-*') {
    throw 'Runtime restore paths failed the ownership checks; no changes were made.'
}
if (Test-Path -LiteralPath $userPath) {
    $link = [IO.DirectoryInfo]::new($userPath)
    $resolved = $link.ResolveLinkTarget($true)
    if ($null -ne $resolved) {
        if ([IO.Path]::GetFullPath($resolved.FullName) -ne $profilePath) {
            throw 'The Sims 4 path no longer points at the prepared isolated profile; backup preserved.'
        }
        [IO.Directory]::Delete($userPath)
    } elseif (Test-Path -LiteralPath $backupPath) {
        throw 'An unexpected ordinary directory occupies the profile path while the original backup exists.'
    }
}
if (Test-Path -LiteralPath $backupPath) {
    [IO.Directory]::Move($backupPath, $userPath)
} elseif (-not (Test-Path -LiteralPath $userPath -PathType Container)) {
    throw 'Original backup is missing and the profile path is absent; isolated files were preserved.'
}
if (Test-Path -LiteralPath $profilePath) { Remove-Item -LiteralPath $profilePath -Recurse -Force }
if (Test-Path -LiteralPath $artifactRoot) { Remove-Item -LiteralPath $artifactRoot -Recurse -Force }
Remove-Item -LiteralPath $statePath -Force
[pscustomobject]@{ result = 'restored'; originalProfileRestored = (Test-Path -LiteralPath $userPath -PathType Container); temporaryProfileRemoved = -not (Test-Path -LiteralPath $profilePath); runtimeArtifactRemoved = -not (Test-Path -LiteralPath $artifactRoot) } | ConvertTo-Json
