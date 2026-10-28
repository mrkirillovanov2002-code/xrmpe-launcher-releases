param([Parameter(Mandatory=$true)][string]$LauncherPath)
$ErrorActionPreference = 'Stop'
$target = (Resolve-Path -LiteralPath $LauncherPath).Path
$folder = Split-Path $target
if ([IO.Path]::GetFileName($target) -ine 'Xrmpe.Launcher.exe' -or !(Test-Path -LiteralPath (Join-Path $folder 'Xrmpe.Launcher.dll'))) { throw 'Not a Custom Launcher installation.' }
$shell = New-Object -ComObject WScript.Shell
try {
    # Run in the ORIGINAL interactive user's context, including when Setup elevated.
    # Repair existing aliases only; never create or delete a desktop shortcut.
    foreach ($file in Get-ChildItem -LiteralPath ([Environment]::GetFolderPath('DesktopDirectory')) -Filter '*.lnk' -File) {
        $link = $shell.CreateShortcut($file.FullName)
        try {
            if ([IO.Path]::GetFileName($link.TargetPath) -ine 'Xrmpe.Launcher.exe') { continue }
            $oldFolder = Split-Path $link.TargetPath
            if ($oldFolder -notmatch '^[A-Za-z]:\\') { continue }
            if (!(Test-Path -LiteralPath (Join-Path $oldFolder 'Xrmpe.Launcher.dll')) -or !(Test-Path -LiteralPath (Join-Path $oldFolder 'Avalonia.Controls.dll'))) { continue }
            $link.TargetPath = $target
            $link.WorkingDirectory = $folder
            $link.IconLocation = (Join-Path $folder 'Assets\launcher.ico') + ',0'
            # Keep filename, arguments, hotkey and other user-defined properties.
            $link.Save()
        } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null }
    }
} finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null }
