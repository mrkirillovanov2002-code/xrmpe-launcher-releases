[CmdletBinding()]
param([switch]$CreateDesktopShortcut)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repo 'artifacts\installer-preview'
$app = Join-Path $output 'app'
$fixture = Join-Path $output ('packaging-test-' + [Guid]::NewGuid().ToString('N'))
# Non-ASCII paths without depending on Windows PowerShell 5.1 script file encoding.
$copy = Join-Path $fixture (([char]0x0418).ToString() + ' application with spaces')
$desktop = Join-Path $fixture (([char]0x0414).ToString() + ' desktop with spaces')
$sentinel = Join-Path $fixture 'separate-game\savedgames\keep.sav'
$shell = New-Object -ComObject WScript.Shell
function Assert-Link([string]$LinkPath, [string]$Exe) {
    $link = $shell.CreateShortcut($LinkPath)
    try {
        if ($link.TargetPath -ne $Exe) { throw 'Shortcut target mismatch.' }
        if ($link.WorkingDirectory -ne [IO.Path]::GetDirectoryName($Exe)) { throw 'Shortcut working directory mismatch.' }
        if ($link.IconLocation -ne ((Join-Path ([IO.Path]::GetDirectoryName($Exe)) 'Assets\launcher.ico') + ',0')) { throw 'Shortcut icon mismatch.' }
    }
    finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null }
}
function Assert-ShortcutLaunch([string]$LinkPath, [string]$Exe) {
    $before = @(Get-Process -Name 'Xrmpe.Launcher' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $launched = @()
    try {
        Start-Process -FilePath $LinkPath -WindowStyle Hidden
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            Start-Sleep -Milliseconds 200
            $launched = @(Get-Process -Name 'Xrmpe.Launcher' -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id -and $_.Path -eq $Exe })
            $visibleTitle = @($launched | Where-Object { $_.MainWindowTitle -like '*Custom Launcher*' })
        } while ($visibleTitle.Count -eq 0 -and [DateTime]::UtcNow -lt $deadline)
        if ($visibleTitle.Count -ne 1) { throw 'Launcher window did not initialize after shortcut launch.' }
        Write-Output ('PASS: shortcut launched window, PID ' + $visibleTitle[0].Id)
    }
    finally {
        # Only the new launcher instances started by this test, never game/Steam processes.
        foreach ($process in $launched) {
            if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        }
    }
}
try {
    [IO.Directory]::CreateDirectory($fixture) | Out-Null
    Copy-Item -LiteralPath $app -Destination $copy -Recurse
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($sentinel)) | Out-Null
    [IO.File]::WriteAllText($sentinel, 'preserve game saves')
    $script = Join-Path $copy 'Create-Shortcut.ps1'
    $linkPath = Join-Path $desktop 'xrMPE Launcher.lnk'
    $originalExe = Join-Path $app 'Xrmpe.Launcher.exe'
    $copiedExe = Join-Path $copy 'Xrmpe.Launcher.exe'
    & $script -LauncherPath $originalExe -ShortcutDirectory $desktop | Out-Null
    Assert-Link $linkPath $originalExe
    & $script -ShortcutDirectory $desktop | Out-Null
    & $script -ShortcutDirectory $desktop | Out-Null
    Assert-Link $linkPath $copiedExe
    if (@(Get-ChildItem -LiteralPath $desktop -Filter '*.lnk').Count -ne 1) { throw 'Duplicate shortcuts were created.' }
    Write-Output 'PASS: target update, repeated creation, one shortcut, Unicode/spaces, working directory and icon.'
    Assert-ShortcutLaunch $linkPath $copiedExe
    if ($CreateDesktopShortcut) {
        $productionScript = Join-Path $app 'Create-Shortcut.ps1'
        & $productionScript | Out-Null
        & $productionScript | Out-Null
        $realLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'xrMPE Launcher.lnk'
        Assert-Link $realLink $originalExe
        Assert-ShortcutLaunch $realLink $originalExe
        Write-Output ('Desktop shortcut ready: ' + $realLink)
    }
    # This checks only portable-folder cleanup, not Inno uninstall (compiler unavailable).
    $checkedCopy = [IO.Path]::GetFullPath($copy)
    $checkedFixture = [IO.Path]::GetFullPath($fixture).TrimEnd('\') + '\'
    if (-not $checkedCopy.StartsWith($checkedFixture, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup target.' }
    Remove-Item -LiteralPath $checkedCopy -Recurse -Force
    if ([IO.File]::ReadAllText($sentinel) -ne 'preserve game saves') { throw 'Separate game data was modified.' }
    Write-Output 'PASS: portable application folder removed, separate game save preserved. Inno install/uninstall NOT tested.'
}
finally {
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    $absolute = [IO.Path]::GetFullPath($fixture)
    $allowed = [IO.Path]::GetFullPath($output).TrimEnd('\') + '\'
    if (-not $absolute.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($absolute)).StartsWith('packaging-test-')) { throw 'Unsafe test cleanup target.' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
