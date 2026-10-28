$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$release = Join-Path $repo 'artifacts\installer-release-0.3.1'
$setup = Join-Path $release 'xrMPE-Launcher-Setup.exe'
$logs = Join-Path $release 'test-logs'
[IO.Directory]::CreateDirectory($logs) | Out-Null
$testRoot = Join-Path $repo ('artifacts\installer-031-test-evidence\install-' + [Guid]::NewGuid().ToString('N'))
$install = Join-Path $testRoot 'Установка с пробелами'
$registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{EB475631-868E-40C0-B134-0F6DB93D551A}_is1'
$otherKeys = @($registry, $registry.Replace('HKCU:','HKLM:'), $registry.Replace('Software\','Software\WOW6432Node\'), $registry.Replace('HKCU:','HKLM:').Replace('Software\','Software\WOW6432Node\'))
if ($otherKeys | Where-Object { Test-Path -LiteralPath $_ }) { throw 'Real registered installation exists; test will not overwrite it.' }
$mutex = $null
if ([Threading.Mutex]::TryOpenExisting('Local\xrMPE.CustomLauncher.Running', [ref]$mutex)) { $mutex.Dispose(); throw 'Close launcher windows before install/uninstall testing.' }
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$profile = Join-Path $env:LOCALAPPDATA 'Xrmpe.CustomLauncher'
[IO.Directory]::CreateDirectory($profile) | Out-Null
$sentinel = Join-Path $profile ('installer-031-test-' + [Guid]::NewGuid().ToString('N') + '.keep')
[IO.File]::WriteAllText($sentinel,'keep user data')
$settings = @{}
Get-ChildItem -LiteralPath $profile -Filter '*.json' | Where-Object Name -ne 'self-update-result.json' | ForEach-Object { $settings[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName).Hash }
$desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'xrMPE Launcher.lnk'
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'xrMPE Launcher\xrMPE Launcher.lnk'
$backups = @()
foreach ($link in @($desktop,$menu)) {
    if (Test-Path -LiteralPath $link) { $backup = Join-Path $testRoot ('link-'+$backups.Count+'.lnk'); Copy-Item -LiteralPath $link -Destination $backup; $backups += @{Path=$link;Backup=$backup} }
}
$shell = New-Object -ComObject WScript.Shell
$common = @('/SP-','/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/LANG=russian','/TASKS=desktopicon')
$ownedInstallation = $false
function Note([string]$message) { $message | Tee-Object -FilePath (Join-Path $logs 'packaging-tests.txt') -Append }
function Invoke-Setup([string]$exe,[string[]]$arguments) {
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Setup/uninstall failed: $($process.ExitCode)" }
}
function Assert-Shortcut([string]$path) {
    if (!(Test-Path -LiteralPath $path)) { throw "Missing shortcut: $path" }
    $link = $shell.CreateShortcut($path)
    try {
        if ($link.TargetPath -ne (Join-Path $install 'Xrmpe.Launcher.exe') -or $link.WorkingDirectory -ne $install -or $link.IconLocation -ne ((Join-Path $install 'Assets\launcher.ico')+',0')) { throw 'Shortcut target/workdir/icon incorrect.' }
    } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null }
}
function Assert-Launch([string]$linkPath,[string]$version) {
    Start-Process -FilePath $linkPath -WindowStyle Hidden
    $ours = @()
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            Start-Sleep -Milliseconds 150
            $ours = @(Get-Process Xrmpe.Launcher -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $install 'Xrmpe.Launcher.exe') })
            $ready = @($ours | Where-Object { $_.MainWindowTitle -like '*xrMPE*' })
        } while ($ready.Count -eq 0 -and [DateTime]::UtcNow -lt $deadline)
        if ($ready.Count -ne 1) { throw 'Installed window not ready.' }
        if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install 'Xrmpe.Launcher.dll')).FileVersion -ne $version) { throw 'Wrong actual installed assembly version.' }
        $clr = @($ready[0].Modules | Where-Object ModuleName -eq 'coreclr.dll')
        if ($clr.Count -ne 1 -or !$clr[0].FileName.StartsWith($install+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Launcher is not using its bundled .NET runtime.' }
    } finally { foreach ($process in $ours) { if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() } } }
}
function Uninstall-Owned([string]$logName) {
    $uninstaller = Join-Path $install 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller) { Invoke-Setup $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $logs $logName)+'"')) }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ((Test-Path -LiteralPath $registry) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (Test-Path -LiteralPath $registry) { throw 'Windows uninstall registration remains.' }
}
try {
    Invoke-Setup $setup ($common + @(('/DIR="'+$install+'"'),('/LOG="'+(Join-Path $logs 'install.log')+'"')))
    $ownedInstallation = $true
    $registration = Get-ItemProperty -LiteralPath $registry
    if ($registration.DisplayVersion -ne '0.3.1') { throw 'Windows installed-app version incorrect.' }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $release 'app') -Recurse -File) {
        $relative = $file.FullName.Substring((Join-Path $release 'app').Length+1)
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $install $relative)).Hash) { throw "Installed payload differs: $relative" }
    }
    Assert-Shortcut $desktop; Assert-Shortcut $menu
    Assert-Launch $desktop '0.3.1.0'; Assert-Launch $menu '0.3.1.0'
    Note 'PASS: clean Inno installation 0.3.1, all publication files, Windows registration, desktop/Start shortcuts, Unicode/spaces, bundled local CLR.'
    & dotnet run --project (Join-Path $repo 'tests\SelfUpdate.Integration') -c Release -- $repo '--installed' (Join-Path $release 'app') (Join-Path $repo 'artifacts\installer-031-test-evidence\Release-0.3.2') $install | Tee-Object -FilePath (Join-Path $logs 'installed-self-update.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Installed self-update integration failed.' }
    Assert-Launch $desktop '0.3.2.0'
    Note 'PASS: installed 0.3.1 -> test 0.3.2 via HTTP :8787, restart, profile/history preservation, rollback, ACL refusal; shortcut still launches updated version.'
    $uninstallCommand = (Get-ItemProperty -LiteralPath $registry).QuietUninstallString
    if ($uninstallCommand -notlike ('*'+(Join-Path $install 'unins000.exe')+'*')) { throw 'Windows uninstall command changed.' }
    Uninstall-Owned 'uninstall-after-update.log'; $ownedInstallation = $false
    foreach ($name in (Get-Content -LiteralPath (Join-Path $repo 'src\Xrmpe.SelfUpdate\AllowedFiles.txt'))) { if (Test-Path -LiteralPath (Join-Path $install $name)) { throw "Managed launcher file survived uninstall: $name" } }
    if (Test-Path -LiteralPath (Join-Path $install '.xrmpe-launcher.lock')) { throw 'Updater lock file left behind.' }
    if (!(Test-Path -LiteralPath (Join-Path $install 'user-settings.json')) -or !(Test-Path -LiteralPath (Join-Path $install 'saves\keep.sav')) -or !(Test-Path -LiteralPath $sentinel)) { throw 'User files were removed.' }
    if ((Test-Path -LiteralPath $desktop) -or (Test-Path -LiteralPath $menu)) { throw 'Installed shortcuts survived uninstall.' }
    foreach ($key in $settings.Keys) { if ((Get-FileHash -LiteralPath $key).Hash -ne $settings[$key]) { throw "User setting changed: $key" } }
    Note 'PASS: original registered Windows uninstaller removes self-updated files, shortcuts and registration; preserves user settings, history and saves.'
    Invoke-Setup $setup ($common + @(('/DIR="'+$install+'"'),('/LOG="'+(Join-Path $logs 'reinstall.log')+'"'))); $ownedInstallation = $true
    Invoke-Setup $setup ($common + @(('/DIR="'+$install+'"'),('/LOG="'+(Join-Path $logs 'repeat-install.log')+'"')))
    Assert-Shortcut $desktop; Assert-Shortcut $menu; Assert-Launch $menu '0.3.1.0'
    Uninstall-Owned 'second-uninstall.log'; $ownedInstallation = $false
    Note 'PASS: reinstall, repeated install, launch and second uninstall.'
} finally {
    # Restore pre-test shortcuts exactly; never touch an unrelated installation.
    Get-Process Xrmpe.Launcher -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $install 'Xrmpe.Launcher.exe') } | Stop-Process
    if ($ownedInstallation) { Uninstall-Owned 'cleanup-uninstall.log' }
    foreach ($item in $backups) { [IO.Directory]::CreateDirectory((Split-Path $item.Path)) | Out-Null; Copy-Item -LiteralPath $item.Backup -Destination $item.Path -Force }
    if (Test-Path -LiteralPath $sentinel) { Remove-Item -LiteralPath $sentinel }
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
}
