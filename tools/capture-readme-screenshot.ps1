#Requires -Version 7
<#
.SYNOPSIS
  Builds VideoAbridge, composes a sample fixture MP4 from CC0 stills, and captures docs/readme-screenshot.png.
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
$stillsDir = Join-Path $repoRoot 'tools\readme-sample\images'
$stills = @(
    (Join-Path $stillsDir '01-valley-flowers.jpg'),
    (Join-Path $stillsDir '02-landscape-clouds.jpg'),
    (Join-Path $stillsDir '03-city-skyline.jpg'),
    (Join-Path $stillsDir '04-mountains.jpg')
)
New-Item -ItemType Directory -Force -Path $fixturesDir | Out-Null

foreach ($still in $stills) {
    if (-not (Test-Path -LiteralPath $still)) {
        throw "Missing README sample still: $still (see tools/readme-sample/ATTRIBUTION.md)."
    }
}

if ($ForceFixture -or -not (Test-Path $sampleMp4)) {
    Write-Host "Generating $sampleMp4 from CC0 stills..."
    # Four scenes with 0.5s crossfades. Hold 3.0s each → output ~10.5s
    # (4*3 - 3*0.5). Letterbox/pillarbox to 1280x720.
    $prep = "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p"
    $vf = @(
        "[0:v]$prep[v0]"
        "[1:v]$prep[v1]"
        "[2:v]$prep[v2]"
        "[3:v]$prep[v3]"
        "[v0][v1]xfade=transition=fade:duration=0.5:offset=2.5[x01]"
        "[x01][v2]xfade=transition=fade:duration=0.5:offset=5.0[x02]"
        "[x02][v3]xfade=transition=fade:duration=0.5:offset=7.5[vout]"
    ) -join ';'

    & ffmpeg -y -hide_banner -loglevel error `
        -loop 1 -t 3 -i $stills[0] `
        -loop 1 -t 3 -i $stills[1] `
        -loop 1 -t 3 -i $stills[2] `
        -loop 1 -t 3 -i $stills[3] `
        -f lavfi -i "sine=frequency=220:sample_rate=44100:duration=11" `
        -filter_complex $vf `
        -map '[vout]' -map '4:a' `
        -t 10.5 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest `
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
