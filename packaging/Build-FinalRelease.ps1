[CmdletBinding()]
param([string]$ISCCPath = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repo 'artifacts\final-release'
$portable = Join-Path $output 'Portable'
$app = Join-Path $portable 'app'
if (-not (Test-Path -LiteralPath $ISCCPath)) { throw 'ISCC.exe not found; specify -ISCCPath.' }
& dotnet publish (Join-Path $repo 'src\Xrmpe.Launcher') -c Release -r win-x64 --self-contained true -o $app
if ($LASTEXITCODE -ne 0) { throw 'Publication failed.' }
if (Get-ChildItem -LiteralPath $app -Recurse -File | Where-Object { $_.Name -eq 'xrEngine.exe' -or $_.Extension -eq '.addon' }) {
    throw 'Game binaries/archives unexpectedly found in the launcher publication.'
}
# A Unicode filename with ASCII-only CMD contents; no codepage dependence in the script.
$cmdName = 'Запустить xrMPE Launcher.cmd'
[IO.File]::WriteAllText((Join-Path $portable $cmdName), ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Start-Portable.cmd')) -replace "`r?`n", "`r`n"), [Text.Encoding]::ASCII)
Copy-Item -LiteralPath (Join-Path $repo 'docs\FINAL-README-RU.md') -Destination (Join-Path $output 'README-RU.md')
Copy-Item -LiteralPath (Join-Path $repo 'docs\FINAL-README-RU.md') -Destination (Join-Path $portable 'README-RU.md')
$updateHelp = [IO.File]::ReadAllText((Join-Path $repo 'docs\UPDATES.md')).Replace('— localhost preview', '— инструкция').Replace('`artifacts/updates-localhost/Xrmpe.Launcher.exe`', '`Xrmpe.Launcher.exe`')
[IO.File]::WriteAllText((Join-Path $app 'UPDATES-RU.md'), $updateHelp, [Text.UTF8Encoding]::new($false))
& $ISCCPath '/Qp' ("/DPublishDir=" + $app) ("/DInstallerOutput=" + $output) '/DInstallerName=xrMPE-Launcher-Setup' (Join-Path $PSScriptRoot 'xrMPE-Launcher.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$setup = Join-Path $output 'xrMPE-Launcher-Setup.exe'
if (-not (Test-Path -LiteralPath $setup)) { throw 'Installer output missing.' }
Get-FileHash -LiteralPath $setup -Algorithm SHA256 | Format-List
Write-Output $output
