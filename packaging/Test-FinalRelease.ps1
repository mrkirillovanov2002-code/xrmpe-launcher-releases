[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$release = Join-Path $repo 'artifacts\final-release'
$setup = Join-Path $release 'xrMPE-Launcher-Setup.exe'
$testRoot = Join-Path $repo ('artifacts\release-test-' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $testRoot 'Установка с пробелами'
$moved = Join-Path $testRoot 'Переносимая папка с пробелами'
$unrelated = Join-Path $testRoot 'Другая рабочая папка'
$game = Join-Path $testRoot 'separate-game'
$profile = Join-Path $env:LOCALAPPDATA 'Xrmpe.CustomLauncher'
$profileSentinel = Join-Path $profile ('release-test-' + [Guid]::NewGuid().ToString('N') + '.keep')
$desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'xrMPE Launcher.lnk'
$startLink = Join-Path ([Environment]::GetFolderPath('Programs')) 'xrMPE Launcher\xrMPE Launcher.lnk'
$registryPaths = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{EB475631-868E-40C0-B134-0F6DB93D551A}_is1', 'HKCU:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{EB475631-868E-40C0-B134-0F6DB93D551A}_is1')
if ($registryPaths | Where-Object { Test-Path -LiteralPath $_ }) { throw 'A real installation already exists. Test stopped to avoid modifying it.' }
$mutex = $null
if ([Threading.Mutex]::TryOpenExisting('Local\xrMPE.CustomLauncher.Running', [ref]$mutex)) { $mutex.Dispose(); throw 'Close all launcher windows before testing install/uninstall.' }
$backups = @()
$shell = New-Object -ComObject WScript.Shell
$installed = $false
$report = [Collections.Generic.List[string]]::new()
$logs = Join-Path $repo 'artifacts\final-release-test-logs'
[IO.Directory]::CreateDirectory($logs) | Out-Null
function Note([string]$Text) { $report.Add($Text); Write-Output $Text }
function Run-Setup([string]$Executable, [string[]]$Arguments, [bool]$ExpectSuccess = $true) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru -Wait
    foreach ($log in Get-ChildItem -LiteralPath $testRoot -Filter '*.log' -ErrorAction SilentlyContinue) { Copy-Item -LiteralPath $log.FullName -Destination (Join-Path $logs $log.Name) -Force }
    if ($ExpectSuccess -and $process.ExitCode -ne 0) { throw ('Installer/uninstaller exit code: ' + $process.ExitCode) }
    return $process.ExitCode
}
function Assert-Link([string]$LinkPath, [string]$Exe) {
    if (-not (Test-Path -LiteralPath $LinkPath)) { throw ('Shortcut missing: ' + $LinkPath) }
    $link = $shell.CreateShortcut($LinkPath)
    try {
        if ($link.TargetPath -ne $Exe -or $link.WorkingDirectory -ne [IO.Path]::GetDirectoryName($Exe) -or
            $link.IconLocation -ne ((Join-Path ([IO.Path]::GetDirectoryName($Exe)) 'Assets\launcher.ico') + ',0')) { throw 'Installed shortcut metadata mismatch.' }
    }
    finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null }
}
function Assert-Launch([scriptblock]$Action, [string]$Exe) {
    $before = @(Get-Process -Name Xrmpe.Launcher -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $ours = @()
    try {
        & $Action
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            Start-Sleep -Milliseconds 200
            $ours = @(Get-Process -Name Xrmpe.Launcher -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id -and $_.Path -eq $Exe })
            $ready = @($ours | Where-Object { $_.MainWindowTitle -like '*Custom Launcher*' })
        } while ($ready.Count -eq 0 -and [DateTime]::UtcNow -lt $deadline)
        if ($ready.Count -ne 1) { throw 'Application window did not initialize.' }
    }
    finally { foreach ($process in $ours) { if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() } } }
}
function Remove-TestRoot {
    $absolute = [IO.Path]::GetFullPath($testRoot)
    $allowed = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')).TrimEnd('\') + '\'
    if (-not $absolute.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or -not ([IO.Path]::GetFileName($absolute)).StartsWith('release-test-')) { throw 'Unsafe cleanup target.' }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
try {
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    [IO.Directory]::CreateDirectory($unrelated) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $game 'bin')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $game 'savedgames')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $game 'bin\xrEngine.exe'), 'fixture only, never execute')
    [IO.File]::WriteAllText((Join-Path $game 'savedgames\keep.sav'), 'preserve save')
    [IO.Directory]::CreateDirectory($profile) | Out-Null
    [IO.File]::WriteAllText($profileSentinel, 'preserve launcher settings')
    $existingSettings = @{}
    Get-ChildItem -LiteralPath $profile -Recurse -File | Where-Object { $_.FullName -notmatch '\\Logs\\' } | ForEach-Object { $existingSettings[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName).Hash }
    foreach ($link in @($desktopLink,$startLink)) {
        if (Test-Path -LiteralPath $link) {
            $backup = Join-Path $testRoot ('shortcut-backup-' + $backups.Count + '.lnk')
            Copy-Item -LiteralPath $link -Destination $backup
            $backups += [PSCustomObject]@{ Path=$link; Backup=$backup }
        }
    }
    $common = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/LANG=russian','/TASKS=desktopicon')
    $bad = Run-Setup $setup ($common + @(('/DIR="' + $game + '"'),('/LOG="' + (Join-Path $testRoot 'guard.log') + '"'))) $false
    if ($bad -le 0 -or (Test-Path -LiteralPath (Join-Path $game 'Xrmpe.Launcher.exe'))) { throw 'Game-directory install guard failed.' }
    $guardLog = [IO.File]::ReadAllText((Join-Path $testRoot 'guard.log'))
    if ($guardLog -notlike '*Game files and profiles must remain separate*' -or $guardLog -like '*User chose OK*') { throw 'Silent game-directory guard must exit without an interactive dialog.' }
    Note 'PASS: actual installer refused the game folder (silent mode included).'
    $installArgs = $common + @(('/DIR="' + $install + '"'),('/LOG="' + (Join-Path $testRoot 'install.log') + '"'))
    Run-Setup $setup $installArgs | Out-Null
    $installed = $true
    $exe = Join-Path $install 'Xrmpe.Launcher.exe'
    if (-not (Test-Path -LiteralPath $exe)) { throw 'Installed EXE missing.' }
    $sourceApp = Join-Path $release 'Portable\app'
    foreach ($file in Get-ChildItem -LiteralPath $sourceApp -Recurse -File) {
        $relative = $file.FullName.Substring($sourceApp.Length).TrimStart('\')
        if ((Get-FileHash -LiteralPath (Join-Path $install $relative)).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw ('Installed file differs: '+$relative) }
    }
    if (@($registryPaths | Where-Object { Test-Path -LiteralPath $_ }).Count -ne 1) { throw 'Expected one installed-app registration.' }
    Assert-Link $desktopLink $exe
    Assert-Link $startLink $exe
    Assert-Launch { Start-Process -FilePath $desktopLink -WindowStyle Hidden } $exe
    Assert-Launch { Start-Process -FilePath $startLink -WindowStyle Hidden } $exe
    Note 'PASS: installed all publication files, one Windows registration, desktop and Start Menu shortcut launches (Unicode/spaces).'
    Run-Setup $setup ($common + @(('/DIR="' + $install + '"'),('/LOG="' + (Join-Path $testRoot 'reinstall.log') + '"'))) | Out-Null
    Assert-Link $desktopLink $exe
    Assert-Link $startLink $exe
    if (@($registryPaths | Where-Object { Test-Path -LiteralPath $_ }).Count -ne 1) { throw 'Duplicate app registration after reinstall.' }
    Assert-Launch { Start-Process -FilePath $desktopLink -WindowStyle Hidden } $exe
    Note 'PASS: repeated installation, same shortcuts and one installed-app registration; relaunch works.'
    $uninstaller = Join-Path $install 'unins000.exe'
    Run-Setup $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $testRoot 'uninstall.log') + '"')) | Out-Null
    $installed = $false
    if ((Test-Path -LiteralPath $exe) -or (Test-Path -LiteralPath $desktopLink) -or (Test-Path -LiteralPath $startLink) -or @($registryPaths | Where-Object { Test-Path -LiteralPath $_ }).Count -gt 0) { throw 'Uninstall left launcher files, shortcuts or registration.' }
    if ([IO.File]::ReadAllText((Join-Path $game 'savedgames\keep.sav')) -ne 'preserve save' -or [IO.File]::ReadAllText($profileSentinel) -ne 'preserve launcher settings') { throw 'Uninstall changed saves or settings.' }
    foreach ($path in $existingSettings.Keys) { if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path).Hash -ne $existingSettings[$path]) { throw ('Existing setting changed: '+$path) } }
    Note 'PASS: real Inno uninstall removed app, shortcuts and registration; separate game/save and existing launcher settings preserved.'
    Copy-Item -LiteralPath (Join-Path $release 'Portable') -Destination $moved -Recurse
    $cmd = Join-Path $moved 'Запустить xrMPE Launcher.cmd'
    $movedExe = Join-Path $moved 'app\Xrmpe.Launcher.exe'
    $commandArgs = '/d /s /c ""' + $cmd + '""'
    Assert-Launch { Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\cmd.exe') -ArgumentList $commandArgs -WorkingDirectory $unrelated -WindowStyle Hidden } $movedExe
    Note 'PASS: copied Portable folder launches via root CMD from an unrelated working directory, with Cyrillic and spaces.'
    $report | Set-Content -LiteralPath (Join-Path $release 'packaging-tests.txt') -Encoding UTF8
    foreach ($log in @('guard.log','install.log','reinstall.log','uninstall.log')) { Copy-Item -LiteralPath (Join-Path $testRoot $log) -Destination (Join-Path $logs $log) }
}
finally {
    $safeCleanup = $true
    if ($installed -and (Test-Path -LiteralPath (Join-Path $install 'unins000.exe'))) {
        try { Run-Setup (Join-Path $install 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $testRoot 'recovery.log') + '"')) | Out-Null }
        catch { $safeCleanup = $false; Write-Warning ('Test uninstall requires recovery: ' + $install) }
    }
    foreach ($backup in $backups) { [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup.Path)) | Out-Null; Copy-Item -LiteralPath $backup.Backup -Destination $backup.Path -Force }
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    if (Test-Path -LiteralPath $profileSentinel) { Remove-Item -LiteralPath $profileSentinel }
    if ($safeCleanup) { Remove-TestRoot }
}
