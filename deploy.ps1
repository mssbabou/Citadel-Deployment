#Requires -Version 5.1
<#
.SYNOPSIS
  Citadel deployment client (Windows/PowerShell).

.DESCRIPTION
  Reads .env from the script's directory and deploys a zip to the Citadel
  server. Auth is HMAC-SHA256 over (timestamp || LF || profile || LF || zip bytes).

.PARAMETER Source
  Path to a directory, single file, or .zip to deploy. Overrides SOURCE in .env.

.PARAMETER DryRun
  Build and sign the payload and ask the server what would happen, but don't
  mutate anything.

.PARAMETER List
  List profiles registered on the server.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)] [string] $Source,
    [switch] $DryRun,
    [switch] $List,
    [switch] $Help,
    [switch] $Version,
    [switch] $Quiet
)

$ErrorActionPreference = 'Stop'
$ScriptVersion = '2.0.0'

function Show-Usage {
@'
deploy.ps1 — Citadel deployment client

Usage:
  .\deploy.ps1 [-Source <path>]         Deploy a directory, file, or .zip
  .\deploy.ps1 -DryRun [-Source <path>] Ask the server what would happen; no mutation
  .\deploy.ps1 -List                    List profiles registered on the server
  .\deploy.ps1 -Help
  .\deploy.ps1 -Version

Config: .env in the script's directory with AUTH_TOKEN, DEPLOY_URL, PROFILE,
and optional SOURCE.
'@
}

if ($Help)    { Show-Usage; exit 0 }
if ($Version) { Write-Host "deploy.ps1 $ScriptVersion"; exit 0 }

function Say($msg) { if (-not $Quiet) { Write-Host $msg } }

# ─── Load .env ────────────────────────────────────────────────────────────────

$envFile = Join-Path $PSScriptRoot '.env'
if (-not (Test-Path $envFile)) {
    Write-Error "Error: .env file not found at $envFile"
    exit 1
}

$config = @{}
foreach ($line in Get-Content $envFile) {
    if ($line -match '^\s*#' -or $line -match '^\s*$') { continue }
    $i = $line.IndexOf('=')
    if ($i -lt 0) { continue }
    $k = $line.Substring(0, $i).Trim()
    $v = $line.Substring($i + 1).Trim()
    if (($v.StartsWith('"') -and $v.EndsWith('"')) -or
        ($v.StartsWith("'") -and $v.EndsWith("'"))) {
        $v = $v.Substring(1, $v.Length - 2)
    }
    $config[$k] = $v
}

$AuthToken = $config['AUTH_TOKEN']
$DeployUrl = $config['DEPLOY_URL']
$ProfileName   = $config['PROFILE']
if (-not $Source) { $Source = $config['SOURCE'] }

if (-not $AuthToken) { Write-Error "Error: AUTH_TOKEN not set in $envFile"; exit 1 }
if (-not $DeployUrl) { Write-Error "Error: DEPLOY_URL not set in $envFile"; exit 1 }
if (-not $ProfileName)   { Write-Error "Error: PROFILE not set in $envFile"; exit 1 }

$RootUrl = $DeployUrl -replace '/deploy$',''
$TempDir = if ($env:TEMP) { $env:TEMP } elseif ($env:TMPDIR) { $env:TMPDIR } else { '/tmp' }
$CurlBin = if ($IsWindows) { 'curl.exe' } else { 'curl' }

function To-Hex([byte[]] $bytes) {
    ([BitConverter]::ToString($bytes)).Replace('-', '').ToLower()
}

function Sign-V2([string] $context, [byte[]] $body, [long] $ts) {
    $key    = [Text.Encoding]::UTF8.GetBytes($AuthToken)
    $prefix = [Text.Encoding]::UTF8.GetBytes("$ts`n$context`n")
    $hmac   = [Security.Cryptography.HMACSHA256]::new($key)
    try {
        $null = $hmac.TransformBlock($prefix, 0, $prefix.Length, $null, 0)
        if ($body -and $body.Length -gt 0) {
            $null = $hmac.TransformBlock($body, 0, $body.Length, $null, 0)
        }
        $null = $hmac.TransformFinalBlock(@(), 0, 0)
        return (To-Hex $hmac.Hash)
    } finally {
        $hmac.Dispose()
    }
}

# ─── --list ────────────────────────────────────────────────────────────────────

if ($List) {
    $ts  = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $sig = Sign-V2 'GET /profiles' $null $ts
    $out = & $CurlBin -fsSL `
        -H "X-Protocol: v2" `
        -H "X-Timestamp: $ts" `
        -H "X-Signature: $sig" `
        "$RootUrl/profiles"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to list profiles (exit $LASTEXITCODE)"
        exit 1
    }
    $out
    exit 0
}

# ─── Deploy / dry-run ─────────────────────────────────────────────────────────

if (-not $Source) { Write-Error "Error: SOURCE not set (pass -Source or set in .env)"; exit 1 }
if (-not (Test-Path $Source)) { Write-Error "Error: source path does not exist: $Source"; exit 1 }

$Source   = $Source.TrimEnd('\', '/')
$tempZip  = Join-Path $TempDir ("deploy_" + [IO.Path]::GetRandomFileName().Replace('.', '') + ".zip")
$respFile = Join-Path $TempDir ("citadel_resp_" + [IO.Path]::GetRandomFileName().Replace('.', '') + ".txt")

try {
    Add-Type -Assembly System.IO.Compression
    Add-Type -Assembly System.IO.Compression.FileSystem

    if ($Source -like '*.zip') {
        Say "Copying zip file: $Source"
        Copy-Item $Source $tempZip
    } elseif (Test-Path $Source -PathType Container) {
        Say "Zipping directory: $Source"
        $srcFull = [IO.Path]::GetFullPath($Source)
        $name    = [IO.Path]::GetFileName($srcFull)
        $zip     = [IO.Compression.ZipFile]::Open($tempZip, 'Create')
        try {
            Get-ChildItem $srcFull -Recurse -File | ForEach-Object {
                $entry = $name + '/' + $_.FullName.Substring($srcFull.Length + 1).Replace('\', '/')
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entry) | Out-Null
            }
        } finally { $zip.Dispose() }
    } else {
        Say "Zipping file: $Source"
        $srcFull = [IO.Path]::GetFullPath($Source)
        $name    = [IO.Path]::GetFileName($srcFull)
        $zip     = [IO.Compression.ZipFile]::Open($tempZip, 'Create')
        try {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $srcFull, $name) | Out-Null
        } finally { $zip.Dispose() }
    }

    $zipBytes = [IO.File]::ReadAllBytes($tempZip)
    Say ("Created zip: {0:N2} MB" -f ($zipBytes.Length / 1MB))

    $ts  = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $sig = Sign-V2 $ProfileName $zipBytes $ts

    $curlArgs = @(
        '-s', '--no-buffer',
        '-o', $respFile,
        '-w', '%{http_code}',
        '-X', 'POST',
        '-H', "X-Protocol: v2",
        '-H', "X-Timestamp: $ts",
        '-H', "X-Signature: $sig",
        '-H', "X-Profile: $ProfileName",
        '-F', "file=@$tempZip",
        $DeployUrl
    )
    if ($DryRun) {
        Say "Dry run — no side effects will be applied"
        $curlArgs = @('-H', 'X-DryRun: true') + $curlArgs
    }

    Say "Deploying to $DeployUrl (profile: $ProfileName)"
    $httpCode = & $CurlBin @curlArgs

    if (Test-Path $respFile) {
        Get-Content $respFile | ForEach-Object { Write-Host $_ }
    }

    if ($DryRun) {
        if ($httpCode -ne '200') {
            Write-Error "DEPLOY FAILED: HTTP $httpCode"
            exit 1
        }
        Say "Dry run OK"
        exit 0
    }

    $lastLine = if (Test-Path $respFile) {
        (Get-Content $respFile | Where-Object { $_ } | Select-Object -Last 1)
    } else { $null }

    if ($httpCode -eq '200' -and $lastLine -eq 'OK') {
        Say "Done"
        exit 0
    }
    Write-Error "DEPLOY FAILED: HTTP $httpCode, last line: $lastLine"
    exit 1

} finally {
    if (Test-Path $tempZip)  { Remove-Item $tempZip  -Force -ErrorAction SilentlyContinue }
    if (Test-Path $respFile) { Remove-Item $respFile -Force -ErrorAction SilentlyContinue }
}
