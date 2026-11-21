#Requires -Version 5.1
<#
.SYNOPSIS
  Packs and pushes the videoabridge Chocolatey package for a GitHub Release zip.

.NOTES
  Patches placeholders before pack, then restores them:
  - tools/chocolateyInstall.ps1: __CHECKSUM64__
  - videoabridge.nuspec: __ICON_REF__ → v{Version} (jsDelivr pin; no separate CDN publish)
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)]
  [string] $Version,

  [Parameter(Mandatory = $true)]
  [string] $ZipPath,

  [Parameter(Mandatory = $true)]
  [string] $ApiKey,

  [string] $PackagingDir = (Split-Path -Parent $MyInvocation.MyCommand.Definition),

  [string] $OutputDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'videoabridge-chocolatey'),

  [string] $Source = 'https://push.chocolatey.org/'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ZipPath)) {
  throw "Release zip not found: $ZipPath"
}

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
  throw 'Chocolatey API key is empty.'
}

$installScript = Join-Path $PackagingDir 'tools\chocolateyInstall.ps1'
$nuspec = Join-Path $PackagingDir 'videoabridge.nuspec'
if (-not (Test-Path -LiteralPath $installScript)) {
  throw "Install script not found: $installScript"
}
if (-not (Test-Path -LiteralPath $nuspec)) {
  throw "Nuspec not found: $nuspec"
}

$checksum = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$iconRef = "v$Version"
Write-Host "SHA256 ($ZipPath): $checksum"
Write-Host "iconUrl jsDelivr ref: $iconRef"

$installOriginal = Get-Content -LiteralPath $installScript -Raw
$nuspecOriginal = Get-Content -LiteralPath $nuspec -Raw
if ($installOriginal -notmatch '__CHECKSUM64__') {
  throw 'chocolateyInstall.ps1 is missing the __CHECKSUM64__ placeholder.'
}
if ($nuspecOriginal -notmatch '__ICON_REF__') {
  throw 'videoabridge.nuspec is missing the __ICON_REF__ placeholder.'
}

Set-Content -LiteralPath $installScript -Value ($installOriginal.Replace('__CHECKSUM64__', $checksum)) -NoNewline
Set-Content -LiteralPath $nuspec -Value ($nuspecOriginal.Replace('__ICON_REF__', $iconRef)) -NoNewline

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

try {
  Push-Location $PackagingDir
  try {
    & choco pack $nuspec --version=$Version --outputdirectory $OutputDirectory --no-progress
    if ($LASTEXITCODE -ne 0) {
      throw "choco pack failed with exit code $LASTEXITCODE"
    }
  }
  finally {
    Pop-Location
  }
}
finally {
  # Restore placeholders so local working trees / retries stay clean.
  Set-Content -LiteralPath $installScript -Value $installOriginal -NoNewline
  Set-Content -LiteralPath $nuspec -Value $nuspecOriginal -NoNewline
}

$nupkg = Join-Path $OutputDirectory "videoabridge.$Version.nupkg"
if (-not (Test-Path -LiteralPath $nupkg)) {
  throw "nupkg not found: $nupkg"
}

Write-Host "Pushing $nupkg to $Source"
& choco push $nupkg --source=$Source --api-key=$ApiKey --no-progress
if ($LASTEXITCODE -ne 0) {
  throw "choco push failed with exit code $LASTEXITCODE"
}

Write-Host "Published videoabridge $Version to Chocolatey."
