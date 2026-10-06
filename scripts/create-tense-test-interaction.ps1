param(
    [string]$OutputRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$cliDll = Join-Path $repoRoot 'src\S4MT.Cli\bin\Debug\net10.0\s4mt.dll'
$project = Join-Path $repoRoot 'examples\StressedMoodTestInteraction'
$resources = Join-Path $project 'resources'
if (-not (Test-Path -LiteralPath $cliDll -PathType Leaf)) { throw "Required file not found: $cliDll" }
[IO.Directory]::CreateDirectory($resources) | Out-Null

$interactionName = 'S4MT:TestTense_OnBed'
$snippetName = 'S4MT:TestTense_OnBed_Injector'
$interactionPath = Join-Path $resources 'test-tense-interaction.xml'
$snippetPath = Join-Path $resources 'bed-injector-snippet.xml'
$stringsPath = Join-Path $project 'strings.json'

function Invoke-S4mt([string[]]$Arguments) {
    $lines = @(& dotnet $script:cliDll @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $output = ($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
    if ($exitCode -ne 0) { throw "s4mt $($Arguments -join ' ') failed ($exitCode): $output" }
    return $output | ConvertFrom-Json
}

$interactionXml = @"
<I c="ImmediateSuperInteraction" i="interaction" m="interactions.base.immediate_interaction" n="$interactionName" s="1">
  <V n="_saveable" t="disabled" />
  <L n="basic_extras">
    <V t="notification">
      <U n="notification">
        <V n="dialog" t="single"><V n="single" t="literal"><U n="literal"><V n="title" t="enabled"><T n="enabled">0xD5B00002</T></V><V n="text" t="single"><T n="single">0xD5B00003</T></V><E n="urgency">URGENT</E></U></V></V>
        <V n="timing" t="at_beginning" />
      </U>
    </V>
    <V t="buff"><U n="buff"><U n="buff_type"><T n="buff_type">12437<!--Buff_Environment_TenseAmbienceLarge, Mood_Stressed weight 3--></T></U><L n="subject"><E>Actor</E></L></U></V>
    <V t="buff"><U n="buff"><U n="buff_type"><T n="buff_type">12438<!--Buff_Environment_TenseAmbienceMedium, Mood_Stressed weight 2--></T></U><L n="subject"><E>Actor</E></L></U></V>
  </L>
  <T n="display_name">0xD5B00001<!--Ułóż się i zestresuj--></T>
  <T n="pie_menu_priority">10</T>
  <T n="allow_user_directed">True</T>
  <U n="progress_bar_enabled"><T n="bar_enabled">False</T></U>
  <E n="target_type">OBJECT</E>
  <L n="test_globals">
    <V t="sim_info"><U n="sim_info"><V n="ages" t="specified"><L n="specified"><E>TEEN</E><E>YOUNGADULT</E><E>ADULT</E><E>ELDER</E></L></V><V n="species" t="unspecified"/><E n="who">Actor</E></U></V>
  </L>
</I>
"@
$snippetXml = @"
<I c="XmlInjector" i="snippet" m="xml_injector.snippet" n="$snippetName" s="1">
  <L n="add_interactions_to_objects">
    <U>
      <V n="object_selection" t="objects_with_affordance"><U n="objects_with_affordance"><T n="affordance">13094<!--bed_sleep--></T></U></V>
      <L n="_super_affordances"><T>1<!--S4MT:TestTense_OnBed--></T></L>
    </U>
  </L>
</I>
"@
[IO.File]::WriteAllText($interactionPath, $interactionXml, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($snippetPath, $snippetXml, [Text.UTF8Encoding]::new($false))

$interactionValidation = Invoke-S4mt @('tuning', 'validate', '--xml', $interactionPath)
$snippetValidation = Invoke-S4mt @('tuning', 'validate', '--xml', $snippetPath)
$interactionId = ($interactionValidation.nameHashes[0].candidates | Where-Object algorithm -eq 'FNV64-high-bit').hash
$snippetId = ($snippetValidation.nameHashes[0].candidates | Where-Object algorithm -eq 'FNV64-high-bit').hash
if (-not $interactionId -or -not $snippetId) { throw 'Could not calculate tuning instance hashes.' }
$interactionXml = $interactionXml.Replace('s="1"', "s=`"$([Convert]::ToUInt64($interactionId.Substring(2), 16))`"")
$snippetXml = $snippetXml.Replace('s="1"', "s=`"$([Convert]::ToUInt64($snippetId.Substring(2), 16))`"")
$snippetXml = $snippetXml.Replace('<T>1<!--S4MT:TestTense_OnBed--></T>', "<T>$([Convert]::ToUInt64($interactionId.Substring(2), 16))<!--S4MT:TestTense_OnBed--></T>")
[IO.File]::WriteAllText($interactionPath, $interactionXml, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($snippetPath, $snippetXml, [Text.UTF8Encoding]::new($false))

$strings = [ordered]@{ format = 1; tgi = '0x220557DA:0x00000000:0x0F00000000000001'; entries = @(
    @{ key = '0xD5B00001'; text = 'Ułóż się i zestresuj' },
    @{ key = '0xD5B00002'; text = 'Test nastroju' },
    @{ key = '0xD5B00003'; text = 'Interakcja uruchomiona. Sprawdź nastrój Sima.' }
) }
[IO.File]::WriteAllText($stringsPath, ($strings | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$stblPackage = Join-Path $repoRoot '.audit\test-tense-strings.package'
$null = Invoke-S4mt @('stbl', 'build', '--json', $stringsPath, '--output', $stblPackage)
$stblPayload = Join-Path $resources 'test-tense-strings.stbl'
$null = Invoke-S4mt @('resource', 'export', '--package', $stblPackage, '--tgi', '0x220557DA:0x00000000:0x0F00000000000001', '--output', $stblPayload)
$manifest = [ordered]@{ format = 1; resources = @(
    @{ type = '0xE882D22F'; group = '0x00000000'; instance = ('0x{0:X16}' -f [Convert]::ToUInt64($interactionId.Substring(2), 16)); source = 'resources/test-tense-interaction.xml'; encoding = 'utf8' },
    @{ type = '0x7DF2169C'; group = '0x00000000'; instance = ('0x{0:X16}' -f [Convert]::ToUInt64($snippetId.Substring(2), 16)); source = 'resources/bed-injector-snippet.xml'; encoding = 'utf8' },
    @{ type = '0x220557DA'; group = '0x00000000'; instance = '0x0F00000000000001'; source = 'resources/test-tense-strings.stbl'; encoding = 'bytes' }
) }
[IO.File]::WriteAllText((Join-Path $project 'package.json'), ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$validation = Invoke-S4mt @('project', 'validate', '--project', $project)
if (-not $validation.valid -or $validation.resourceCount -ne 3) { throw 'The generated test interaction project failed validation.' }
$outputPath = [IO.Path]::GetFullPath($OutputRoot)
$build = Invoke-S4mt @('project', 'build', '--project', $project, '--output-root', $outputPath)
if (-not $build.valid -or -not (Test-Path -LiteralPath $build.package -PathType Leaf)) { throw 'The generated test interaction package did not build.' }
$packagePath = Join-Path $outputPath 'S4MT-TestTense-OnBed.package'
[IO.File]::Copy($build.package, $packagePath, $true)
$readback = Join-Path $repoRoot '.audit\test-tense-strings-readback.json'
$null = Invoke-S4mt @('stbl', 'export', '--package', $packagePath, '--tgi', '0x220557DA:0x00000000:0x0F00000000000001', '--output', $readback)
$readbackTable = Get-Content -LiteralPath $readback -Raw | ConvertFrom-Json
$readbackEntry = @($readbackTable.entries | Where-Object key -eq '0xD5B00001')
if ($readbackTable.locale -ne 'pl' -or $readbackEntry.Count -ne 1 -or $readbackEntry[0].text -ne 'Ułóż się i zestresuj' -or $readbackTable.entries.Count -ne 3) {
    throw 'The built package did not preserve all Polish interaction strings.'
}

if ($Install) {
    $mods = Join-Path $env:USERPROFILE 'Documents\Electronic Arts\The Sims 4\Mods'
    if (-not (Test-Path -LiteralPath $mods -PathType Container)) { throw "Mods folder not found: $mods" }
    $injector = @(Get-ChildItem -LiteralPath $mods -Filter 'XmlInjector_Script_v*.ts4script' -File)
    if ($injector.Count -eq 0) { throw 'XML Injector script is not installed in the Mods root; cannot install a dependent interaction mod.' }
    $gameProcess = Get-Process -Name TS4_x64 -ErrorAction SilentlyContinue
    if ($gameProcess) { throw 'The Sims 4 is running; close it before installing this package.' }
    [IO.File]::Copy($packagePath, (Join-Path $mods 'S4MT-TestTense-OnBed.package'), $true)
}

[pscustomobject]@{
    result = 'verified'
    project = $project
    package = $packagePath
    installed = [bool]$Install
    interactionTgi = "0xE882D22F:0x00000000:$('0x{0:X16}' -f [Convert]::ToUInt64($interactionId.Substring(2), 16))"
    snippetTgi = "0x7DF2169C:0x00000000:$('0x{0:X16}' -f [Convert]::ToUInt64($snippetId.Substring(2), 16))"
    resources = 3
    projectValidated = $validation.valid
    labelReadBack = $readbackEntry[0].text
    locale = $readbackTable.locale
} | ConvertTo-Json -Depth 5
