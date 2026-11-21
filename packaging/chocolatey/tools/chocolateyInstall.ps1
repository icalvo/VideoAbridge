$ErrorActionPreference = 'Stop'

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$version = $env:ChocolateyPackageVersion
$tag = "v$version"
$zipName = "VideoAbridge-$tag-win-x64.zip"
$url64 = "https://github.com/icalvo/VideoAbridge/releases/download/$tag/$zipName"

# Replaced by the release workflow before choco pack.
$checksum64 = '__CHECKSUM64__'

$packageArgs = @{
  packageName    = $env:ChocolateyPackageName
  unzipLocation  = $toolsDir
  url64bit       = $url64
  checksum64     = $checksum64
  checksumType64 = 'sha256'
}

Install-ChocolateyZipPackage @packageArgs

$exe = Join-Path $toolsDir 'VideoAbridge.exe'
if (-not (Test-Path -LiteralPath $exe)) {
  throw "VideoAbridge.exe not found after unzip under $toolsDir"
}

# GUI app: shim should not wait for process exit.
New-Item -ItemType File -Path ($exe + '.gui') -Force | Out-Null

$programs = [Environment]::GetFolderPath('CommonPrograms')
$shortcut = Join-Path $programs 'VideoAbridge.lnk'
Install-ChocolateyShortcut `
  -ShortcutFilePath $shortcut `
  -TargetPath $exe `
  -WorkingDirectory $toolsDir `
  -Description 'VideoAbridge'
