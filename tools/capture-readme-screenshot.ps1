#Requires -Version 7
<#
.SYNOPSIS
  Builds VideoAbridge, generates a sample fixture MP4, and captures docs/readme-screenshot.png.
#>
[CmdletBinding()]
param(
    [switch] $ForceFixture
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

function Require-Command([string] $Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name was not found on PATH. Install FFmpeg and ensure ffmpeg/ffprobe are available."
    }
}

Require-Command ffmpeg
Require-Command ffprobe

$fixturesDir = Join-Path $repoRoot 'tools\fixtures'
$sampleMp4 = Join-Path $fixturesDir 'readme-sample.mp4'
New-Item -ItemType Directory -Force -Path $fixturesDir | Out-Null

if ($ForceFixture -or -not (Test-Path $sampleMp4)) {
    Write-Host "Generating $sampleMp4 ..."
    & ffmpeg -y -hide_banner -loglevel error `
        -f lavfi -i "smptehdbars=size=1280x720:rate=30" `
        -f lavfi -i "sine=frequency=440:sample_rate=44100" `
        -t 10 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest `
        $sampleMp4
    if ($LASTEXITCODE -ne 0) {
        throw "ffmpeg failed to generate the sample fixture."
    }
}

Write-Host 'Building VideoAbridge (Release)...'
dotnet build (Join-Path $repoRoot 'src\VideoAbridge\VideoAbridge.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'VideoAbridge build failed.' }

Write-Host 'Building ReadmeScreenshot (Release)...'
dotnet build (Join-Path $repoRoot 'tools\ReadmeScreenshot\ReadmeScreenshot.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'ReadmeScreenshot build failed.' }

$appExe = Join-Path $repoRoot 'src\VideoAbridge\bin\Release\net11.0-windows\VideoAbridge.exe'
if (-not (Test-Path $appExe)) {
    throw "Built app not found at $appExe"
}

$cacheDir = Join-Path ([System.IO.Path]::GetTempPath()) ('VideoAbridgeReadmeCache_' + [guid]::NewGuid().ToString('N'))
$outPng = Join-Path $repoRoot 'docs\readme-screenshot.png'
New-Item -ItemType Directory -Force -Path (Split-Path $outPng) | Out-Null
New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null

try {
    Write-Host 'Capturing README screenshot...'
    dotnet run --project (Join-Path $repoRoot 'tools\ReadmeScreenshot\ReadmeScreenshot.csproj') -c Release --no-build -- `
        --app $appExe `
        --video $sampleMp4 `
        --cache $cacheDir `
        --output $outPng `
        --settle-ms 3000
    if ($LASTEXITCODE -ne 0) { throw 'ReadmeScreenshot failed.' }
}
finally {
    if (Test-Path $cacheDir) {
        Remove-Item -Recurse -Force $cacheDir -ErrorAction SilentlyContinue
    }
}

Write-Host "Done: $outPng"
