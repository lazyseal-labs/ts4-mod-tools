param(
    [string]$GameRoot = 'C:\Program Files\EA Games\The Sims 4',
    [string]$OutputPackage = (Join-Path $PSScriptRoot '..\dist\S4MT-StressedMood-Green.package')
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$fullOutput = [System.IO.Path]::GetFullPath($OutputPackage)
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("s4mt-stressed-mood-" + [guid]::NewGuid().ToString('N'))
$xmlPath = Join-Path $tempRoot 'Mood_Stressed.xml'
$packagePath = Join-Path $tempRoot 'S4MT-StressedMood-Green.package'
$combinedPackage = Join-Path $GameRoot 'Data\Simulation\SimulationDeltaBuild0.package'
$combinedTgi = '0x62E94D38:0x00000000:0xF1352E740C4710BA'
$moodTgi = '0x03B33DDF:0x00000000:0xB17201C249771AF4'
$snapshot = Join-Path $repoRoot 'examples\StressedMoodGreen\.s4mt\schema'
$snapshotHash = '168d8f1e5a73ff5fbd0d362463802fe6781a6e91f70c768b0937d6360b448326'

function Invoke-CliJson([string[]]$Arguments) {
    $output = & dotnet run --no-build --project (Join-Path $repoRoot 'src\S4MT.Cli') -- @Arguments
    if ($LASTEXITCODE -ne 0) { throw "S4MT command failed: $($Arguments -join ' ')" }
    return ($output | ConvertFrom-Json)
}

if (-not (Test-Path -LiteralPath $combinedPackage -PathType Leaf)) {
    throw "The installed SimulationDeltaBuild0.package was not found: $combinedPackage"
}
if (Test-Path -LiteralPath $fullOutput) {
    throw "Output already exists; choose another path or remove it first: $fullOutput"
}

New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
try {
    $extract = Invoke-CliJson @(
        'tuning', 'extract', '--package', $combinedPackage, '--combined-tgi', $combinedTgi,
        '--name', 'Mood_Stressed', '--output', $xmlPath
    )
    if ($extract.tgi -ne $moodTgi -or $extract.module -ne 'statistics.mood') {
        throw "Extracted unexpected tuning identity: $($extract | ConvertTo-Json -Compress)"
    }

    $document = [System.Xml.Linq.XDocument]::Load($xmlPath)
    $unresolvedReferences = @($document.Root.Descendants() | Where-Object { $_.Name.LocalName -eq 'r' })
    if ($unresolvedReferences.Count -ne 0) { throw "Extracted tuning still contains $($unresolvedReferences.Count) shared-table references." }
    $colorList = @($document.Root.Descendants() | Where-Object {
        $_.Name.LocalName -eq 'L' -and [string]$_.Attribute('n').Value -eq 'mood_colors'
    })
    if ($colorList.Count -ne 1) { throw "Expected one mood_colors field, found $($colorList.Count)." }
    $rgb = @($colorList[0].Descendants() | Where-Object {
        $_.Name.LocalName -eq 'T' -and [string]$_.Attribute('n').Value -in @('x', 'y', 'z')
    })
    if ($rgb.Count -ne 6) { throw "Expected two RGB colors (6 values), found $($rgb.Count)." }
    foreach ($value in $rgb) {
        switch ([string]$value.Attribute('n').Value) {
            'x' { $value.Value = '0' }
            'y' { $value.Value = '255' }
            'z' { $value.Value = '0' }
        }
    }
    $portraitFrames = @($document.Root.Descendants() | Where-Object {
        $_.Name.LocalName -eq 'L' -and [string]$_.Attribute('n').Value -eq 'portrait_frames'
    })
    if ($portraitFrames.Count -ne 1) { throw "Expected one portrait_frames field, found $($portraitFrames.Count)." }
    $frameNames = @($portraitFrames[0].Elements() | Where-Object { $_.Name.LocalName -eq 'T' })
    if ($frameNames.Count -ne 2) { throw "Expected two portrait frame labels, found $($frameNames.Count)." }
    $frameNames[0].Value = 'Inspired'
    $frameNames[1].Value = 'Imaginative'
    $settings = [System.Xml.XmlWriterSettings]::new()
    $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $settings.Indent = $false
    $writer = [System.Xml.XmlWriter]::Create($xmlPath, $settings)
    try { $document.Save($writer) } finally { $writer.Dispose() }

    $validation = Invoke-CliJson @(
        'tuning', 'validate', '--xml', $xmlPath, '--tgi', $moodTgi,
        '--snapshot', $snapshot, '--game-version', '1.128.90.1030', '--snapshot-sha256', $snapshotHash
    )
    if (-not $validation.valid -or -not $validation.schemaValidation.valid) {
        throw 'Extracted mood tuning failed XML or pinned TDESC validation.'
    }

    $build = Invoke-CliJson @(
        'tuning', 'build', '--tgi', $moodTgi, '--xml', $xmlPath, '--output', $packagePath,
        '--snapshot', $snapshot, '--game-version', '1.128.90.1030', '--snapshot-sha256', $snapshotHash
    )
    if (-not $build.verified) { throw 'S4MT did not verify the generated package.' }

    $inspection = Invoke-CliJson @('inspect', '--package', $packagePath)
    $resource = @($inspection.resources | Where-Object {
        $_.type -eq '0x03B33DDF' -and $_.group -eq '0x00000000' -and $_.instance -eq '0xB17201C249771AF4'
    })
    if ($inspection.resourceCount -ne 1 -or $resource.Count -ne 1) {
        throw 'Built package did not contain exactly the expected tuning TGI.'
    }

    $outputDirectory = [System.IO.Path]::GetDirectoryName($fullOutput)
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    Copy-Item -LiteralPath $packagePath -Destination $fullOutput
    [pscustomobject]@{
        result = 'verified'
        outputPackage = $fullOutput
        tuningName = 'Mood_Stressed'
        changedField = 'mood_colors'
        changedRgb = '0,255,0'
        changedField2 = 'portrait_frames'
        portraitFrames = 'Inspired,Imaginative'
        sourcePackage = $combinedPackage
        tgi = $moodTgi
        extractedElementCount = $extract.extractedElementCount
        schemaCoverage = $validation.schemaValidation.coverage
        packageSha256 = (Get-FileHash -LiteralPath $fullOutput -Algorithm SHA256).Hash
        packageReadBack = $true
    } | ConvertTo-Json -Compress
} finally {
    $resolvedTempRoot = [System.IO.Path]::GetFullPath($tempRoot)
    $resolvedSystemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if (-not $resolvedTempRoot.StartsWith($resolvedSystemTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove temporary files outside the system temporary directory: $resolvedTempRoot"
    }
    if (Test-Path -LiteralPath $resolvedTempRoot) {
        Remove-Item -LiteralPath $resolvedTempRoot -Recurse -Force
    }
}
