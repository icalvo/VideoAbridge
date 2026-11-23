$ErrorActionPreference = 'Stop'

$programs = [Environment]::GetFolderPath('CommonPrograms')
$shortcut = Join-Path $programs 'VideoAbridge.lnk'
if (Test-Path -LiteralPath $shortcut) {
  Remove-Item -LiteralPath $shortcut -Force
}

$extensions = @('.mp4', '.mkv', '.mov', '.avi', '.wmv', '.webm')
foreach ($ext in $extensions) {
  $verbKey = "HKLM:\Software\Classes\SystemFileAssociations\$ext\shell\VideoAbridge"
  if (Test-Path -LiteralPath $verbKey) {
    Remove-Item -LiteralPath $verbKey -Recurse -Force
  }

  $userVerbKey = "HKCU:\Software\Classes\SystemFileAssociations\$ext\shell\VideoAbridge"
  if (Test-Path -LiteralPath $userVerbKey) {
    Remove-Item -LiteralPath $userVerbKey -Recurse -Force
  }
}
