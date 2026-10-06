param([switch]$KeepArtifacts)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cliDll = Join-Path $repoRoot 'src\S4MT.Cli\bin\Debug\net10.0\s4mt.dll'
$gameRoot = 'C:\Program Files\EA Games\The Sims 4'
$sourcePackage = Join-Path $gameRoot 'Data\Client\Strings_POL_PL.package'
$gameIndex = Join-Path $env:LOCALAPPDATA 'S4MT\cache\game-index.jsonl.gz'
$tgi = '0x220557DA:0x00000000:0x0F54935F2685AD48'
$key = '0x0DFED87C'
$newText = 'Nowa gra (S4MT test)'
foreach ($path in @($cliDll, $sourcePackage, $gameIndex)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required local mod-authoring fixture does not exist: $path"
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

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "s4mt-agent-authoring-$([Guid]::NewGuid().ToString('N'))"
$tempRootFull = [IO.Path]::GetFullPath($tempRoot)
$tempParentFull = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $tempRootFull.StartsWith($tempParentFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Temporary authoring directory escaped the system temp root: $tempRootFull"
}
[IO.Directory]::CreateDirectory($tempRootFull) | Out-Null

try {
    $baseJson = Join-Path $tempRootFull 'base-stbl.json'
    $modJson = Join-Path $tempRootFull 'mod-stbl.json'
    $stblPackage = Join-Path $tempRootFull 'stbl.package'
    $stblBytes = Join-Path $tempRootFull 'stbl.bin'
    $project = Join-Path $tempRootFull 'PolishMenuLabel'
    $projectResources = Join-Path $project 'resources'
    $outputRoot = Join-Path $tempRootFull 'built'

    $export = Invoke-S4mtJson @('stbl', 'export', '--package', $sourcePackage, '--tgi', $tgi, '--output', $baseJson)
    $baseline = Get-Content -LiteralPath $baseJson -Raw | ConvertFrom-Json
    $matches = @($baseline.entries | Where-Object key -eq $key)
    if ($matches.Count -ne 1 -or $matches[0].text -ne 'Nowa gra') {
        throw "Expected exactly one baseline string $key with text 'Nowa gra'; found $($matches.Count)."
    }
    $baselineEntries = @($baseline.entries | ForEach-Object { "$($_.key)`0$($_.text)" })
    $mod = Get-Content -LiteralPath $baseJson -Raw | ConvertFrom-Json
    $mod.entries | Where-Object key -eq $key | ForEach-Object { $_.text = $newText }
    [IO.File]::WriteAllText($modJson, ($mod | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

    $stblBuild = Invoke-S4mtJson @('stbl', 'build', '--json', $modJson, '--output', $stblPackage)
    $null = Invoke-S4mtJson @('resource', 'export', '--package', $stblPackage, '--tgi', $tgi, '--output', $stblBytes)

    $null = Invoke-S4mtJson @('project', 'init', '--name', 'PolishMenuLabel', '--path', $project)
    [IO.Directory]::CreateDirectory($projectResources) | Out-Null
    $projectPayload = Join-Path $projectResources 'polish-menu.stbl'
    [IO.File]::Copy($stblBytes, $projectPayload)
    $projectManifest = [ordered]@{
        format = 1
        resources = @(@{
            type = '0x220557DA'; group = '0x00000000'; instance = '0x0F54935F2685AD48'
            source = 'resources/polish-menu.stbl'; encoding = 'bytes'
        })
    }
    [IO.File]::WriteAllText(
        (Join-Path $project 'package.json'),
        ($projectManifest | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))

    $validation = Invoke-S4mtJson @('project', 'validate', '--project', $project)
    if (-not $validation.valid -or $validation.resourceCount -ne 1) {
        throw "The clean project did not validate its one STBL resource: $($validation | ConvertTo-Json -Compress)"
    }
    $build = Invoke-S4mtJson @('project', 'build', '--project', $project, '--output-root', $outputRoot)
    if (-not $build.valid -or $build.resourceCount -ne 1 -or -not (Test-Path -LiteralPath $build.package -PathType Leaf)) {
        throw "Project build did not produce the expected single-resource package: $($build | ConvertTo-Json -Compress)"
    }

    $builtJson = Join-Path $tempRootFull 'built-stbl.json'
    $null = Invoke-S4mtJson @('stbl', 'export', '--package', $build.package, '--tgi', $tgi, '--output', $builtJson)
    $builtTable = Get-Content -LiteralPath $builtJson -Raw | ConvertFrom-Json
    $builtEntries = @($builtTable.entries | ForEach-Object { "$($_.key)`0$($_.text)" })
    $difference = @(Compare-Object -ReferenceObject $baselineEntries -DifferenceObject $builtEntries)
    $editedEntry = @($builtTable.entries | Where-Object key -eq $key)
    if ($builtTable.entries.Count -ne $baseline.entries.Count -or
        $difference.Count -ne 2 -or $editedEntry.Count -ne 1 -or $editedEntry[0].text -ne $newText) {
        throw 'Built STBL did not preserve the exact table while changing only the target string.'
    }

    $gameConflict = Invoke-S4mtJson @('conflict', 'game', '--package', $build.package, '--index', $gameIndex)
    if ($gameConflict.conflictingResourceCount -ne 1 -or
        $gameConflict.conflicts[0].tgi -ne $tgi -or
        $gameConflict.precedenceVerified) {
        throw 'Game conflict report did not identify the single expected override with precedence left unverified.'
    }

    [pscustomobject]@{
        result = 'verified'
        brief = "Change Polish main-menu label '$($matches[0].text)' to '$newText' while preserving every other entry."
        sourceTgi = $export.tgi
        sourceLocale = $export.locale
        sourceEntryCount = $export.entryCount
        changedKey = $key
        changedEntryCount = ($difference.Count / 2)
        projectValidated = $validation.valid
        packageVerified = $build.valid
        builtResourceCount = $build.resourceCount
        conflictCount = $gameConflict.conflictingResourceCount
        conflictTgi = $gameConflict.conflicts[0].tgi
        packagePath = $build.package
        artifactRoot = $tempRootFull
        runtimePrecedenceVerified = $gameConflict.precedenceVerified
        runtimeBehaviorTested = $false
        packageArtifactCleanedAfterVerification = -not $KeepArtifacts
    } | ConvertTo-Json -Depth 8
}
finally {
    if (-not $KeepArtifacts -and (Test-Path -LiteralPath $tempRootFull -PathType Container) -and
        $tempRootFull.StartsWith($tempParentFull, [StringComparison]::OrdinalIgnoreCase)) {
        [IO.Directory]::Delete($tempRootFull, $true)
    }
}
