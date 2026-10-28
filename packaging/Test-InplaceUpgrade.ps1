$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$release = Join-Path $repo 'artifacts\installer-upgrade-fix-0.3.1'
$setup = Join-Path $release 'xrMPE-Launcher-Setup.exe'
$legacySetup = Join-Path $repo 'artifacts\final-release\xrMPE-Launcher-Setup.exe'
$root = Join-Path $repo ('artifacts\installer-upgrade-test-evidence\'+[Guid]::NewGuid().ToString('N'))
$logs = Join-Path $release 'test-logs'
[IO.Directory]::CreateDirectory($root) | Out-Null
[IO.Directory]::CreateDirectory($logs) | Out-Null
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{EB475631-868E-40C0-B134-0F6DB93D551A}_is1'
foreach($path in @($key,$key.Replace('HKCU:','HKLM:'),$key.Replace('Software\','Software\WOW6432Node\'),$key.Replace('HKCU:','HKLM:').Replace('Software\','Software\WOW6432Node\'))) { if(Test-Path -LiteralPath $path){throw 'Real registered installation exists; test refuses to alter it.'} }
$mutex=$null
if([Threading.Mutex]::TryOpenExisting('Local\xrMPE.CustomLauncher.Running',[ref]$mutex)){$mutex.Dispose();throw 'Close launcher windows before testing.'}
$desktopDir=[Environment]::GetFolderPath('DesktopDirectory')
$desktop=Join-Path $desktopDir 'xrMPE Launcher.lnk'
$custom=Join-Path $desktopDir 'Мой ПДА xrMPE.lnk'
$menu=Join-Path ([Environment]::GetFolderPath('Programs')) 'xrMPE Launcher\xrMPE Launcher.lnk'
$alias=Join-Path $desktopDir 'Дополнительный xrMPE тест.lnk'
$originalGameLink=Join-Path $desktopDir 'Original game protected test.lnk'
if((Test-Path -LiteralPath $alias) -or (Test-Path -LiteralPath $originalGameLink)){throw 'Reserved test shortcut already exists.'}
$backups=@()
foreach($path in @($desktop,$custom,$menu)) {if(Test-Path -LiteralPath $path){$save=Join-Path $root ('link-'+$backups.Count+'.lnk');Copy-Item -LiteralPath $path -Destination $save;$backups+=@{Path=$path;Save=$save}}}
$shell=New-Object -ComObject WScript.Shell
$profile=Join-Path $env:LOCALAPPDATA 'Xrmpe.CustomLauncher'
$settings=@{}
Get-ChildItem -LiteralPath $profile -Filter '*.json' -ErrorAction SilentlyContinue | Where-Object Name -ne 'self-update-result.json' | ForEach-Object {$settings[$_.FullName]=(Get-FileHash -LiteralPath $_.FullName).Hash}
$ownedRoots=[Collections.Generic.List[string]]::new()
$common=@('/SP-','/CURRENTUSER','/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/LANG=russian','/TASKS=desktopicon')
function Note($text){$text | Tee-Object -FilePath (Join-Path $logs 'upgrade-tests.txt') -Append}
function Run($exe,$parameters,[bool]$success=$true){$process=Start-Process -FilePath $exe -ArgumentList $parameters -PassThru -Wait -WindowStyle Hidden;if($success -and $process.ExitCode -ne 0){throw "Setup exit $($process.ExitCode)"};return $process.ExitCode}
function RegisteredRoot { $data=Get-ItemProperty -LiteralPath $key;return [IO.Path]::GetFullPath($data.'Inno Setup: App Path').TrimEnd('\') }
function Version($folder){return [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $folder 'Xrmpe.Launcher.dll')).FileVersion}
function LinkTarget($path){$link=$shell.CreateShortcut($path);try{return $link.TargetPath}finally{[Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)|Out-Null}}
function AssertLink($path,$folder){if(!(Test-Path -LiteralPath $path) -or (LinkTarget $path) -ne (Join-Path $folder 'Xrmpe.Launcher.exe')){throw "Wrong old shortcut: $path"}}
function AssertLaunch($path,$folder,$expectedVersion){
    Start-Process -FilePath $path -WindowStyle Hidden
    $deadline=[DateTime]::UtcNow.AddSeconds(20);$ours=@()
    try{do{Start-Sleep -Milliseconds 150;$ours=@(Get-Process Xrmpe.Launcher -ErrorAction SilentlyContinue|Where-Object {$_.Path -eq (Join-Path $folder 'Xrmpe.Launcher.exe')});$ready=@($ours|Where-Object {$_.MainWindowTitle -like '*xrMPE*'})}while($ready.Count -eq 0 -and [DateTime]::UtcNow -lt $deadline)
        if($ready.Count -ne 1 -or (Version $folder) -ne $expectedVersion){throw 'Old shortcut did not launch expected assembly.'}
        if($expectedVersion -eq '0.3.1.0' -and $ready[0].MainWindowTitle -notlike '*ПДА*'){throw 'Expected redesigned PDA window.'}
    }finally{foreach($process in $ours){if(!$process.HasExited){$process.Kill();$process.WaitForExit()}}}
}
function Uninstall($folder,$name){if(Test-Path -LiteralPath (Join-Path $folder 'unins000.exe')){Run (Join-Path $folder 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+(Join-Path $logs $name)+'"'))|Out-Null};if(Test-Path -LiteralPath $key){throw 'Registration remains after uninstall.'}}
try{
    $old=Join-Path $root 'Настоящая старая установка вне стандартной папки';$ownedRoots.Add($old)
    Run $legacySetup ($common+@(('/DIR="'+$old+'"'),('/LOG="'+(Join-Path $logs 'legacy-install.log')+'"')))|Out-Null
    if((Version $old) -ne '0.2.0.0'){throw 'Legacy setup is not real 0.2.0.'}
    AssertLink $desktop $old;AssertLaunch $desktop $old '0.2.0.0'
    Run $setup ($common+@(('/LOG="'+(Join-Path $logs 'inplace-upgrade.log')+'"')))|Out-Null
    if((RegisteredRoot) -ne $old -or (Version $old) -ne '0.3.1.0'){throw 'Failed to reuse registered old directory without /DIR.'}
    AssertLink $desktop $old;AssertLaunch $desktop $old '0.3.1.0'
    Note 'PASS: actual old installer 0.2.0 -> fixed Setup 0.3.1 WITHOUT /DIR, same nonstandard directory, same old desktop shortcut, actual PDA window.'
    & dotnet run --project (Join-Path $repo 'tests\SelfUpdate.Integration') -c Release -- $repo '--installed' (Join-Path $repo 'artifacts\installer-release-0.3.1\app') (Join-Path $repo 'artifacts\installer-031-test-evidence\Release-0.3.2') $old | Tee-Object -FilePath (Join-Path $logs 'subsequent-self-update.txt')
    if($LASTEXITCODE -ne 0){throw 'Subsequent self-update failed.'}
    AssertLink $desktop $old;AssertLaunch $desktop $old '0.3.2.0'
    Note 'PASS: subsequent self-update via HTTP :8787 keeps EXE directory and old shortcut; settings/history preserved, rollback works.'
    Uninstall $old 'uninstall-after-self-update.log'

    $portable=Join-Path $root 'Переносимая 0.2 без регистрации';$ownedRoots.Add($portable)
    Copy-Item -LiteralPath (Join-Path $repo 'artifacts\final-release\Portable\app') -Destination $portable -Recurse
    $link=$shell.CreateShortcut($custom);$link.TargetPath=Join-Path $portable 'Xrmpe.Launcher.exe';$link.WorkingDirectory=$portable;$link.IconLocation=(Join-Path $portable 'Assets\launcher.ico')+',0';$link.Save();[Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)|Out-Null
    Run $setup ($common+@(('/LOG="'+(Join-Path $logs 'portable-adoption.log')+'"')))|Out-Null
    if((RegisteredRoot) -ne $portable -or (Version $portable) -ne '0.3.1.0'){throw 'Portable shortcut fallback did not adopt correct folder.'}
    AssertLink $custom $portable;AssertLaunch $custom $portable '0.3.1.0'
    if(Test-Path -LiteralPath $desktop){throw 'Duplicate canonical desktop shortcut created.'}
    Note 'PASS: unregistered portable 0.2 detected by renamed Cyrillic desktop shortcut; folder adopted in place, old shortcut name retained, no duplicate.'
    Uninstall $portable 'uninstall-adopted-portable.log'

    $separateOld=Join-Path $root 'Старая версия остаётся';$separateNew=Join-Path $root 'Новая отдельная папка';$ownedRoots.Add($separateOld);$ownedRoots.Add($separateNew)
    Run $legacySetup ($common+@(('/DIR="'+$separateOld+'"')))|Out-Null
    $oldHash=(Get-FileHash -LiteralPath (Join-Path $separateOld 'Xrmpe.Launcher.dll')).Hash
    $link=$shell.CreateShortcut($alias);$link.TargetPath=Join-Path $separateOld 'Xrmpe.Launcher.exe';$link.WorkingDirectory=$separateOld;$link.Save();[Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)|Out-Null
    $link=$shell.CreateShortcut($originalGameLink);$link.TargetPath='C:\Games\STALKER\X-Ray Multiplayer Extension\xrmpe-launcher.exe';$link.Save();[Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)|Out-Null
    $originalLinkHash=(Get-FileHash -LiteralPath $originalGameLink).Hash
    $exit=Run $setup ($common+@(('/DIR="'+$separateNew+'"'),('/LOG="'+(Join-Path $logs 'separate-refused.log')+'"'))) $false
    if($exit -eq 0 -or (Test-Path -LiteralPath (Join-Path $separateNew 'Xrmpe.Launcher.exe'))){throw 'Unconfirmed second installation was not refused.'}
    if((Version $separateOld) -ne '0.2.0.0'){throw 'Old version changed on refusal.'}
    Run $setup ($common+@('/ALLOWSEPARATE=1',('/DIR="'+$separateNew+'"'),('/LOG="'+(Join-Path $logs 'separate-explicit.log')+'"')))|Out-Null
    if((Get-FileHash -LiteralPath (Join-Path $separateOld 'Xrmpe.Launcher.dll')).Hash -ne $oldHash){throw 'Separate old copy modified/deleted.'}
    AssertLink $desktop $separateNew;AssertLaunch $desktop $separateNew '0.3.1.0'
    AssertLink $alias $separateNew
    if((Get-FileHash -LiteralPath $originalGameLink).Hash -ne $originalLinkHash){throw 'Original game launcher shortcut altered.'}
    Note 'PASS: second folder rejected without explicit consent; /ALLOWSEPARATE=1 retains old 0.2 files and retargets same existing desktop shortcut to 0.3.1.'
    Note 'PASS: original-user repair updates existing additional alias without creating a new link; original game launcher shortcut byte-identical.'
    Uninstall $separateNew 'uninstall-separate.log'
    if(!(Test-Path -LiteralPath (Join-Path $separateOld 'Xrmpe.Launcher.exe'))){throw 'New uninstaller removed separate old copy.'}
    foreach($path in $settings.Keys){if((Get-FileHash -LiteralPath $path).Hash -ne $settings[$path]){throw "Setting changed: $path"}}
    Note 'PASS: old separate directory and user profile survive; fixture installation removed.'
}finally{
    Get-Process Xrmpe.Launcher -ErrorAction SilentlyContinue|Where-Object {$_.Path -like ($root+'\*')}|Stop-Process
    if(Test-Path -LiteralPath $key){$registered=RegisteredRoot;if($ownedRoots.Contains($registered)){Uninstall $registered 'cleanup.log'}else{throw 'Unexpected registered path; no cleanup attempted.'}}
    foreach($item in $backups){[IO.Directory]::CreateDirectory((Split-Path $item.Path))|Out-Null;Copy-Item -LiteralPath $item.Save -Destination $item.Path -Force}
    foreach($path in @($alias,$originalGameLink)){if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path}}
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)|Out-Null
}
