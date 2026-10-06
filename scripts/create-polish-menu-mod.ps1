param(
    [string]$OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cliDll = Join-Path $repoRoot 'src\S4MT.Cli\bin\Debug\net10.0\s4mt.dll'
$gameRoot = 'C:\Program Files\EA Games\The Sims 4'
$sourcePackage = Join-Path $gameRoot 'Data\Client\Strings_POL_PL.package'
$gameIndex = Join-Path $env:LOCALAPPDATA 'S4MT\cache\game-index.jsonl.gz'
$tgi = '0x220557DA:0x00000000:0x0F54935F2685AD48'
$key = '0x0DFED87C'
$newText = 'Nowa gra (S4MT test)'
$project = Join-Path $repoRoot 'examples\PolishMenuLabel'
foreach ($path in @($cliDll, $sourcePackage, $gameIndex)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required input not found: $path" }
}
if ((Test-Path -LiteralPath (Join-Path $project 's4mt.json')) -and -not $Force) {
    throw "Refusing to overwrite the existing example project; pass -Force to regenerate this fixture: $project"
}

function Invoke-S4mt([string[]]$Arguments) {
    $lines = @(& dotnet $script:cliDll @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output = ($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    if ($exitCode -ne 0) { throw "s4mt $($Arguments -join ' ') failed ($exitCode): $output" }
    return $output | ConvertFrom-Json
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('s4mt-polish-menu-' + [guid]::NewGuid().ToString('N'))
$tempRootFull = [IO.Path]::GetFullPath($tempRoot)
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $tempRootFull.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Temporary workspace escaped the system temp directory.' }
[IO.Directory]::CreateDirectory($tempRootFull) | Out-Null
try {
    $baselineJson = Join-Path $tempRootFull 'baseline.json'
    $editedJson = Join-Path $tempRootFull 'edited.json'
    $stblPackage = Join-Path $tempRootFull 'stbl.package'
    $payloadPath = Join-Path $tempRootFull 'resource.bin'
    $baseline = Invoke-S4mt @('stbl', 'export', '--package', $sourcePackage, '--tgi', $tgi, '--output', $baselineJson)
    $table = Get-Content -LiteralPath $baselineJson -Raw | ConvertFrom-Json
    $entry = @($table.entries | Where-Object key -eq $key)
    if ($table.locale -ne 'pl' -or $entry.Count -ne 1 -or $entry[0].text -ne 'Nowa gra') {
        throw "The installed Polish game string did not match the expected baseline at $tgi/$key."
    }
    $baselineEntries = @($table.entries | ForEach-Object { "$($_.key)`0$($_.text)" })
    $edited = Get-Content -LiteralPath $baselineJson -Raw | ConvertFrom-Json
    $edited.entries | Where-Object key -eq $key | ForEach-Object { $_.text = $newText }
    [IO.File]::WriteAllText($editedJson, ($edited | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $null = Invoke-S4mt @('stbl', 'build', '--json', $editedJson, '--output', $stblPackage)
    $null = Invoke-S4mt @('resource', 'export', '--package', $stblPackage, '--tgi', $tgi, '--output', $payloadPath)

    $null = Invoke-S4mt @('project', 'init', '--name', 'PolishMenuLabel', '--path', $project)
    $resources = Join-Path $project 'resources'
    [IO.Directory]::CreateDirectory($resources) | Out-Null
    [IO.File]::Copy($payloadPath, (Join-Path $resources 'polish-menu.stbl'), $true)
    $manifest = [ordered]@{ format = 1; resources = @(@{
        type = '0x220557DA'; group = '0x00000000'; instance = '0x0F54935F2685AD48'
        source = 'resources/polish-menu.stbl'; encoding = 'bytes'
    }) }
    [IO.File]::WriteAllText((Join-Path $project 'package.json'), ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

    $validation = Invoke-S4mt @('project', 'validate', '--project', $project)
    if (-not $validation.valid -or $validation.resourceCount -ne 1) { throw 'The generated project failed validation.' }
    $outputPath = [IO.Path]::GetFullPath($OutputRoot)
    $build = Invoke-S4mt @('project', 'build', '--project', $project, '--output-root', $outputPath)
    if (-not $build.valid -or $build.resourceCount -ne 1 -or -not (Test-Path -LiteralPath $build.package -PathType Leaf)) {
        throw 'The generated project did not build its one-resource package.'
    }
    $packagePath = Join-Path $outputPath 'S4MT-PolishMenuLabel.package'
    [IO.File]::Copy($build.package, $packagePath, $true)
    $builtJson = Join-Path $tempRootFull 'built.json'
    $null = Invoke-S4mt @('stbl', 'export', '--package', $packagePath, '--tgi', $tgi, '--output', $builtJson)
    $builtTable = Get-Content -LiteralPath $builtJson -Raw | ConvertFrom-Json
    $builtEntries = @($builtTable.entries | ForEach-Object { "$($_.key)`0$($_.text)" })
    $difference = @(Compare-Object -ReferenceObject $baselineEntries -DifferenceObject $builtEntries)
    $builtEntry = @($builtTable.entries | Where-Object key -eq $key)
    if ($builtTable.entries.Count -ne $baseline.entryCount -or $difference.Count -ne 2 -or $builtEntry.Count -ne 1 -or $builtEntry[0].text -ne $newText) {
        throw 'The package does not preserve all other strings while changing the selected label.'
    }
    $conflicts = Invoke-S4mt @('conflict', 'game', '--package', $packagePath, '--index', $gameIndex)
    if ($conflicts.conflictingResourceCount -ne 1 -or $conflicts.conflicts[0].tgi -ne $tgi -or $conflicts.precedenceVerified) {
        throw 'The built package did not produce the expected single game conflict with precedence unverified.'
    }

    [pscustomobject]@{
        result = 'verified'
        project = $project
        package = $packagePath
        sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
        tgi = $tgi
        locale = $builtTable.locale
        entriesPreserved = $builtTable.entries.Count
        changedKey = $key
        oldText = 'Nowa gra'
        newText = $newText
        projectValidated = $validation.valid
        packageBuiltAndReadBack = $true
        conflictCount = $conflicts.conflictingResourceCount
        precedenceVerified = $conflicts.precedenceVerified
        runtimeTested = $false
    } | ConvertTo-Json -Depth 6
} finally {
    if (Test-Path -LiteralPath $tempRootFull -PathType Container) { [IO.Directory]::Delete($tempRootFull, $true) }
}
