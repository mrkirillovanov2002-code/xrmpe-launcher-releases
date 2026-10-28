param([string]$Output = 'artifacts/installer-release-0.3.1', [string]$ISCCPath = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$destination = [IO.Path]::GetFullPath((Join-Path $repo $Output))
if (Test-Path -LiteralPath $destination) { throw 'Choose a new output directory; existing releases are preserved.' }
if (!(Test-Path -LiteralPath $ISCCPath)) { throw 'ISCC.exe not found.' }
& (Join-Path $PSScriptRoot 'Publish-SelfUpdate.ps1') -Version '0.3.1' -Output ($Output + '/app')
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$app = Join-Path $destination 'app'
foreach ($name in @('Xrmpe.Launcher.exe','Xrmpe.Updater.exe','launcher-release.json','Assets/launcher.ico','hostfxr.dll','hostpolicy.dll','coreclr.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $app $name))) { throw "Missing self-contained dependency: $name" }
}
if (Get-ChildItem -LiteralPath $app -Recurse -File | Where-Object { $_.Name -in @('xrEngine.exe','xrGame.dll','xrNetServer.dll') -or $_.Extension -eq '.addon' }) { throw 'Game files must not be packaged.' }
& $ISCCPath '/Qp' "/DPublishDir=$app" "/DInstallerOutput=$destination" '/DAppVersion=0.3.1' '/DInstallerName=xrMPE-Launcher-Setup' (Join-Path $PSScriptRoot 'xrMPE-Launcher.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
Get-FileHash -LiteralPath (Join-Path $destination 'xrMPE-Launcher-Setup.exe') -Algorithm SHA256 | Format-List
