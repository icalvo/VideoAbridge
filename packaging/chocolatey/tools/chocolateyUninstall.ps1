$ErrorActionPreference = 'Stop'

$programs = [Environment]::GetFolderPath('CommonPrograms')
$shortcut = Join-Path $programs 'VideoAbridge.lnk'
if (Test-Path -LiteralPath $shortcut) {
  Remove-Item -LiteralPath $shortcut -Force
}
