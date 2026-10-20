[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CliPath,

    [Parameter(Mandatory = $true)]
    [string] $GameRoot,

    [Parameter(Mandatory = $true)]
    [uri] $ServiceUri,

    [string] $DotnetPath = "dotnet"
)

$ErrorActionPreference = "Stop"

if (-not $ServiceUri.IsAbsoluteUri -or $ServiceUri.Scheme -notin @("https", "http")) {
    throw "ServiceUri must be an absolute HTTPS URL, or HTTP loopback for local development."
}
if ($ServiceUri.Scheme -eq "http" -and -not $ServiceUri.IsLoopback) {
    throw "Plain HTTP is allowed only for loopback. Use HTTPS for remote homelab access."
}
if (-not (Test-Path -LiteralPath $CliPath -PathType Leaf)) {
    throw "CLI executable or DLL not found: $CliPath"
}
if (-not (Test-Path -LiteralPath $GameRoot -PathType Container)) {
    throw "Game directory not found: $GameRoot"
}

$secureToken = $null
$tokenPointer = [IntPtr]::Zero
$token = $null
$indexPath = Join-Path ([IO.Path]::GetTempPath()) "s4mt-game-index-$([guid]::NewGuid().ToString('N')).jsonl.gz"
$handler = $null
$client = $null
$fileStream = $null
$content = $null
$response = $null

try {
    $secureToken = Read-Host "Homelab cache-admin bearer token" -AsSecureString
    $tokenPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
    try {
        $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenPointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPointer)
        $tokenPointer = [IntPtr]::Zero
    }
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "A bearer token is required."
    }

    if ([IO.Path]::GetExtension($CliPath).Equals(".dll", [StringComparison]::OrdinalIgnoreCase)) {
        & $DotnetPath $CliPath game index --game-root $GameRoot --output $indexPath
    }
    else {
        & $CliPath game index --game-root $GameRoot --output $indexPath
    }
    if ($LASTEXITCODE -ne 0) {
        throw "s4mt game index failed with exit code $LASTEXITCODE."
    }

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(30)
    $client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $token)
    $fileStream = [IO.File]::OpenRead($indexPath)
    $content = [System.Net.Http.StreamContent]::new($fileStream)
    $content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new("application/gzip")

    $baseUrl = $ServiceUri.ToString().TrimEnd("/")
    $uploadUri = [uri]::new("$baseUrl/api/cache/game-index")
    $response = $client.PostAsync($uploadUri, $content).GetAwaiter().GetResult()
    $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        throw "Cache upload failed with HTTP $([int]$response.StatusCode): $responseBody"
    }
    Write-Output $responseBody
}
finally {
    if ($tokenPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPointer)
    }
    if ($response) { $response.Dispose() }
    if ($content) { $content.Dispose() }
    if ($fileStream) { $fileStream.Dispose() }
    if ($client) { $client.Dispose() }
    if ($handler) { $handler.Dispose() }
    if ($secureToken) { $secureToken.Dispose() }
    $token = $null
    if (Test-Path -LiteralPath $indexPath -PathType Leaf) {
        Remove-Item -LiteralPath $indexPath -Force
    }
}
