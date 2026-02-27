# Computes the MD5 checksum for the plugin release zip (Jellyfin manifest format: uppercase hex).
# Usage (from repo root):
#   .\scripts\get-release-checksum.cmd                              # Use .cmd to avoid ExecutionPolicy
#   .\scripts\get-release-checksum.cmd -ZipPath ".\path\to\zip"
#   .\scripts\get-release-checksum.cmd -UpdateManifest
# Or with PowerShell: powershell -ExecutionPolicy Bypass -File .\scripts\get-release-checksum.ps1 -UpdateManifest

param(
    [string]$ZipPath = "",
    [switch]$UpdateManifest
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $ProjectRoot) { $ProjectRoot = (Get-Location).Path }

function Get-MD5Hex {
    param([string]$FilePath)
    $hash = Get-FileHash -Path $FilePath -Algorithm MD5
    return $hash.Hash.ToUpperInvariant()
}

# Resolve zip path
if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    $distZip = Join-Path $ProjectRoot "dist\JellyfinSearchChapters.v1.0.2.0.zip"
    $publishZip = Join-Path $ProjectRoot "JellyfinSearchChapters\bin\Release\net9.0\JellyfinSearchChapters.v1.0.2.0.zip"
    $currentZip = Get-ChildItem -Path $ProjectRoot -Filter "JellyfinSearchChapters.*.zip" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if (Test-Path $distZip) { $ZipPath = $distZip }
    elseif (Test-Path $publishZip) { $ZipPath = $publishZip }
    elseif ($currentZip) { $ZipPath = $currentZip.FullName }
    else {
        Write-Host "No zip found. Build and pack first, or pass -ZipPath '.\path\to\plugin.zip'"
        exit 1
    }
}

if (-not (Test-Path $ZipPath)) {
    Write-Host "Zip not found: $ZipPath"
    exit 1
}

$checksum = Get-MD5Hex -FilePath $ZipPath
Write-Host "MD5 (Jellyfin manifest format): $checksum"
Write-Host "Zip: $ZipPath"

if ($UpdateManifest) {
    $manifestPath = Join-Path $ProjectRoot "manifest.json"
    if (-not (Test-Path $manifestPath)) {
        Write-Host "manifest.json not found at $manifestPath"
        exit 1
    }
    $content = Get-Content $manifestPath -Raw
    # Replace empty checksum for 1.0.2.0
    $old = '"version":"1.0.2.0","targetAbi":"10.11.6.0","sourceUrl":"https://github.com/kotky/jellyfin-search-chapters/releases/download/v1.0.2/JellyfinSearchChapters.v1.0.2.0.zip","checksum":""'
    $new = '"version":"1.0.2.0","targetAbi":"10.11.6.0","sourceUrl":"https://github.com/kotky/jellyfin-search-chapters/releases/download/v1.0.2/JellyfinSearchChapters.v1.0.2.0.zip","checksum":"' + $checksum + '"'
    if ($content.Contains($old)) {
        $content = $content.Replace($old, $new)
        Set-Content -Path $manifestPath -Value $content -NoNewline
        Write-Host "Updated manifest.json checksum for 1.0.2.0 to $checksum"
    }
    else {
        Write-Host "Add to manifest manually: `"checksum`": `"$checksum`""
    }
}
