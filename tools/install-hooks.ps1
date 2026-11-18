#Requires -Version 7
<#
.SYNOPSIS
  Points this repo at .githooks (enables the README screenshot pre-push hook).
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

git config core.hooksPath .githooks
Write-Host "Configured core.hooksPath=.githooks"
Write-Host "Pre-push will refresh docs/readme-screenshot.png (SKIP_README_SCREENSHOT=1 to skip)."
