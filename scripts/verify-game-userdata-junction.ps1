$ErrorActionPreference = 'Stop'

$tempBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$tempRoot = Join-Path $tempBase ('s4mt-game-userdata-junction-' + [guid]::NewGuid().ToString('N'))
$profileRoot = Join-Path $tempRoot 'isolated-profile'
$documentsRoot = Join-Path $tempRoot 'Documents'
$gameUserPath = Join-Path $documentsRoot 'Electronic Arts\The Sims 4'
$markerName = 's4mt-junction-probe.txt'

try {
    New-Item -ItemType Directory -Path $profileRoot -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $gameUserPath) -Force | Out-Null
    $junction = New-Item -ItemType Junction -Path $gameUserPath -Target $profileRoot

    $resolvedTarget = ([System.IO.DirectoryInfo]$junction).ResolveLinkTarget($true)
    if ($null -eq $resolvedTarget -or
        -not [string]::Equals(
            [System.IO.Path]::GetFullPath($resolvedTarget.FullName),
            [System.IO.Path]::GetFullPath($profileRoot),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The game-user junction resolved to an unexpected target.'
    }

    $markerThroughJunction = Join-Path $gameUserPath $markerName
    $markerInTarget = Join-Path $profileRoot $markerName
    [System.IO.File]::WriteAllText($markerThroughJunction, 'isolated profile probe')
    if (-not (Test-Path -LiteralPath $markerInTarget -PathType Leaf) -or
        [System.IO.File]::ReadAllText($markerInTarget) -ne 'isolated profile probe') {
        throw 'A write through the game-user junction did not reach its isolated target.'
    }

    $result = [pscustomobject]@{
        result = 'verified'
        method = 'Windows directory junction at Documents/Electronic Arts/The Sims 4'
        resolvedTarget = $profileRoot
        writesReachIsolatedTarget = $true
        realUserDataTouched = $false
        gameLaunched = $false
    }
    $auditPath = Join-Path (Split-Path -Parent $PSScriptRoot) '.audit\game-userdata-junction-verification.json'
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $auditPath -Encoding utf8
    $result | ConvertTo-Json -Depth 4
}
finally {
    if (Test-Path -LiteralPath $gameUserPath -PathType Container) {
        $linkInfo = [System.IO.DirectoryInfo]::new($gameUserPath)
        $resolvedTarget = $linkInfo.ResolveLinkTarget($true)
        $rootFullPath = [System.IO.Path]::GetFullPath($tempRoot)
        $targetFullPath = if ($null -ne $resolvedTarget) {
            [System.IO.Path]::GetFullPath($resolvedTarget.FullName)
        } else {
            ''
        }
        if ($targetFullPath -ne [System.IO.Path]::GetFullPath($profileRoot)) {
            throw "Refusing to remove an unexpected game-user junction target: $targetFullPath"
        }
        [System.IO.Directory]::Delete($gameUserPath)
    }

    $rootFullPath = [System.IO.Path]::GetFullPath($tempRoot)
    $tempBaseWithSeparator = $tempBase.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $rootFullPath.StartsWith($tempBaseWithSeparator, [StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($rootFullPath) -notlike 's4mt-game-userdata-junction-*') {
        throw "Refusing cleanup outside the expected temporary directory: $rootFullPath"
    }
    if (Test-Path -LiteralPath $rootFullPath -PathType Container) {
        Remove-Item -LiteralPath $rootFullPath -Recurse -Force
    }
}
