#Requires -Version 5.1
<#
.SYNOPSIS
  Packs and pushes the videoabridge Chocolatey package for a GitHub Release zip.
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
if (-not (Test-Path -LiteralPath $installScript)) {
  throw "Install script not found: $installScript"
}

$checksum = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "SHA256 ($ZipPath): $checksum"

$content = Get-Content -LiteralPath $installScript -Raw
if ($content -notmatch '__CHECKSUM64__') {
  throw 'chocolateyInstall.ps1 is missing the __CHECKSUM64__ placeholder.'
}

$patched = $content.Replace('__CHECKSUM64__', $checksum)
Set-Content -LiteralPath $installScript -Value $patched -NoNewline

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$nuspec = Join-Path $PackagingDir 'videoabridge.nuspec'

Push-Location $PackagingDir
try {
  & choco pack $nuspec --version=$Version --outputdirectory $OutputDirectory --no-progress
  if ($LASTEXITCODE -ne 0) {
    throw "choco pack failed with exit code $LASTEXITCODE"
  }
}
finally {
  Pop-Location
  # Restore placeholder so local working trees / retries stay clean.
  Set-Content -LiteralPath $installScript -Value $content -NoNewline
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
