[CmdletBinding()]
param(
    [string]$LauncherPath,
    # Optional override for isolated tests. Default is the current user's real Desktop.
    [string]$ShortcutDirectory = [Environment]::GetFolderPath('DesktopDirectory')
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($LauncherPath)) { $LauncherPath = Join-Path $PSScriptRoot 'Xrmpe.Launcher.exe' }
if (-not (Test-Path -LiteralPath $LauncherPath -PathType Leaf)) {
    throw 'Xrmpe.Launcher.exe was not found. Keep this script beside the published EXE, or pass -LauncherPath.'
}
$exe = (Resolve-Path -LiteralPath $LauncherPath).ProviderPath
$workingDirectory = [IO.Path]::GetDirectoryName($exe)
$icon = Join-Path $workingDirectory 'Assets\launcher.ico'
if (-not (Test-Path -LiteralPath $icon -PathType Leaf)) { throw 'Assets\launcher.ico was not found. Copy the entire published application folder.' }
if ([string]::IsNullOrWhiteSpace($ShortcutDirectory)) { throw 'The Desktop folder is unavailable.' }
[IO.Directory]::CreateDirectory($ShortcutDirectory) | Out-Null
$shortcutPath = Join-Path $ShortcutDirectory 'xrMPE Launcher.lnk'
$shell = New-Object -ComObject WScript.Shell
try {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exe
    $shortcut.Arguments = ''
    $shortcut.WorkingDirectory = $workingDirectory
    $shortcut.IconLocation = $icon + ',0'
    $shortcut.Description = 'xrMPE Custom Launcher'
    $shortcut.WindowStyle = 1
    $shortcut.Save()
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
}
finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
Write-Output $shortcutPath
