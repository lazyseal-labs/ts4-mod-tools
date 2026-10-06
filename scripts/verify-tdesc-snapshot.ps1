param(
    [string]$TemplatesDirectory = 'C:\Program Files (x86)\Sims 4 Studio\Resources\Tuning\Descriptions',
    [string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) '.audit\tdesc-snapshot-verification.json')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$cliDll = Join-Path $repoRoot 'src\S4MT.Cli\bin\Debug\net10.0\s4mt.dll'
foreach ($requiredPath in @($cliDll, $TemplatesDirectory)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required local verification input does not exist: $requiredPath"
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

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "s4mt-tdesc-verify-$([Guid]::NewGuid().ToString('N'))"
$tempRootFull = [IO.Path]::GetFullPath($tempRoot)
$tempParentFull = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $tempRootFull.StartsWith($tempParentFull, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Temporary verification directory escaped the system temp root: $tempRootFull"
}
[IO.Directory]::CreateDirectory($tempRootFull) | Out-Null

try {
    $snapshotPath = Join-Path $tempRootFull 'snapshot'
    $sourceCopy = Join-Path $tempRootFull 'source-copy'
    $first = Invoke-S4mtJson @(
        'tdesc', 'refresh', '--from-dir', $TemplatesDirectory,
        '--game-version', '1.128.90.1030', '--source', 'local Sims 4 Studio CC template verification',
        '--output', $snapshotPath)
    $inspection = Invoke-S4mtJson @('tdesc', 'inspect', '--snapshot', $snapshotPath)
    $second = Invoke-S4mtJson @(
        'tdesc', 'refresh', '--from-dir', $TemplatesDirectory,
        '--game-version', '1.128.90.1030', '--source', 'local Sims 4 Studio CC template verification',
        '--output', $snapshotPath)

    $classNames = @($inspection.classes | ForEach-Object { $_.class })
    if ($inspection.gameVersion -ne '1.128.90.1030' -or
        $inspection.classCount -ne 2 -or
        $classNames -notcontains 'CCRecipeInstance' -or
        $classNames -notcontains 'CCPaintingTextureInstance' -or
        -not $second.reused) {
        throw 'TDesc snapshot did not preserve expected class identities or idempotent reuse.'
    }
    $snapshotTdescCount = @(Get-ChildItem -LiteralPath $snapshotPath -Recurse -Filter '*.tdesc' -File).Count
    if ($snapshotTdescCount -ne 0) {
        throw 'Normalized snapshot unexpectedly copied raw .tdesc files.'
    }

    [IO.Directory]::CreateDirectory($sourceCopy) | Out-Null
    Get-ChildItem -LiteralPath $TemplatesDirectory -Filter '*.tdesc' -File | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $sourceCopy $_.Name)
    }
    $changedTemplate = Join-Path $sourceCopy 'CCRecipeInstance.tdesc'
    [IO.File]::AppendAllText($changedTemplate, "`n<!-- content digest mutation -->`n")
    $differentRefresh = Invoke-S4mt @(
        'tdesc', 'refresh', '--from-dir', $sourceCopy,
        '--game-version', '1.128.90.1030', '--source', 'local mutation verification',
        '--output', $snapshotPath)
    if ($differentRefresh.ExitCode -ne 2) {
        throw 'Snapshot refresh accepted a different source digest at an occupied snapshot path.'
    }

    $indexPath = Join-Path $snapshotPath 'index.json'
    [IO.File]::AppendAllText($indexPath, "`n")
    $corruptInspect = Invoke-S4mt @('tdesc', 'inspect', '--snapshot', $snapshotPath)
    if ($corruptInspect.ExitCode -ne 2) {
        throw 'Snapshot inspect did not reject a modified index.'
    }

    $report = [ordered]@{
        schemaVersion = 1
        verification = 'tdesc-snapshot-import'
        sourceTemplateDirectory = [IO.Path]::GetFullPath($TemplatesDirectory)
        gameVersion = $first.gameVersion
        sourceFileCount = $first.sourceFileCount
        classCount = $first.classCount
        classNames = $classNames
        snapshotSha256 = $first.sha256
        identicalRefreshReused = $second.reused
        changedSourceDigestRejected = ($differentRefresh.ExitCode -eq 2)
        modifiedIndexRejected = ($corruptInspect.ExitCode -eq 2)
        rawSchemasCopied = ($snapshotTdescCount -gt 0)
        wroteOnlyToTemporaryDirectory = $true
    }
    $fullOutputPath = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($fullOutputPath)) -Force | Out-Null
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $fullOutputPath -Encoding utf8
    Get-Content -LiteralPath $fullOutputPath -Raw
}
finally {
    if ((Test-Path -LiteralPath $tempRootFull -PathType Container) -and
        $tempRootFull.StartsWith($tempParentFull, [StringComparison]::OrdinalIgnoreCase)) {
        [IO.Directory]::Delete($tempRootFull, $true)
    }
}
