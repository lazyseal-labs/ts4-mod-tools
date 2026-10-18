param(
    [Parameter(Mandatory)][string]$Receipt,
    [Parameter(Mandatory)][string]$Output
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$record = Get-Content -Raw -LiteralPath $Receipt | ConvertFrom-Json -Depth 80
$lines = [Collections.Generic.List[string]]::new()
$lines.Add("MCP-1 live lane $($record.lane) | recorded stdio transcript")
$lines.Add("Git head $($record.head)")
$lines.Add('')
foreach ($check in $record.checks) { $lines.Add($check) }
$lines.Add('')
foreach ($entry in $record.messages) {
    if ($entry.send.method -eq 'initialize') { $lines.Add('> initialize protocolVersion=2025-11-25') }
    if ($entry.send.method -eq 'tools/list') { $lines.Add('> tools/list') }
    if ($entry.send.method -eq 'tools/call') {
        $lines.Add("> $($entry.send.params.name)")
        $lines.Add(($entry.send.params.arguments | ConvertTo-Json -Compress))
    }
    if ($entry.send.method -eq 'notifications/cancelled') { $lines.Add('> notifications/cancelled') }
    if ($entry.receive.result.tools) {
        $lines.Add('< ' + (($entry.receive.result.tools | ForEach-Object name) -join ', '))
    }
    if ($entry.receive.result.structuredContent) {
        $report = $entry.receive.result.structuredContent
        $lines.Add('< ' + ($report | Select-Object schemaVersion,command,valid,created,fingerprint,reused,error | ConvertTo-Json -Compress -Depth 5))
        if ($report.diagnostics) { $lines.Add(($report.diagnostics | Select-Object -First 1 | ConvertTo-Json -Compress -Depth 5)) }
        if ($report.package) { $lines.Add("package=$($report.package)") }
    }
}
$lines.Add('')
$lines.Add("Host exit $($record.hostExitCode). Full requests, reports and stderr are in receipt.json.")
$text = $lines -join "`n"
$width = 1280
$font = [Drawing.Font]::new('Consolas', 14)
$measureBitmap = [Drawing.Bitmap]::new($width, 1)
$measure = [Drawing.Graphics]::FromImage($measureBitmap)
$size = $measure.MeasureString($text, $font, $width - 48)
$height = [Math]::Max(240, [int]$size.Height + 48)
$measure.Dispose()
$measureBitmap.Dispose()
$bitmap = [Drawing.Bitmap]::new($width, $height)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.Clear([Drawing.Color]::FromArgb(15, 20, 28))
    $graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(220, 232, 243))
    try { $graphics.DrawString($text, $font, $brush, [Drawing.RectangleF]::new(24, 24, $width - 48, $height - 48)) }
    finally { $brush.Dispose() }
    $outputPath = [IO.Path]::GetFullPath($Output)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
    $bitmap.Save($outputPath, [Drawing.Imaging.ImageFormat]::Png)
    Write-Output $outputPath
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
    $font.Dispose()
}
