param([string]$Target)
$ErrorActionPreference = 'Stop'
try {
    if ([string]::IsNullOrWhiteSpace($Target)) { $Target = Read-Host 'Укажите папку СТАРОГО Xrmpe.Launcher.exe (для Portable обычно app)' }
    $Target = [IO.Path]::GetFullPath($Target.Trim().Trim('"'))
    if (!(Test-Path -LiteralPath (Join-Path $Target 'Xrmpe.Launcher.exe')) -and (Test-Path -LiteralPath (Join-Path $Target 'app/Xrmpe.Launcher.exe'))) { $Target = Join-Path $Target 'app' }
    if (!(Test-Path -LiteralPath (Join-Path $Target 'Xrmpe.Launcher.exe'))) { throw 'В папке нет старого Xrmpe.Launcher.exe. Игровую папку выбирать нельзя.' }
    $active = Get-Process Xrmpe.Launcher -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $Target 'Xrmpe.Launcher.exe') }
    if ($active) { throw 'Закройте старый лаунчер и повторите. Скрипт не перезаписывает работающий EXE.' }
    if (Get-Process xrEngine -ErrorAction SilentlyContinue) { throw 'Закройте игру и dedicated-сервер.' }
    $payload = Join-Path $PSScriptRoot 'Payload'
    $stage = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Xrmpe.CustomLauncher\SelfUpdate\' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item -LiteralPath (Join-Path $payload 'launcher-release.json') -Destination $stage
    # The helper validates the closed allow-list, all sizes and hashes before installation.
    $manifest = Get-Content -LiteralPath (Join-Path $payload 'launcher-release.json') -Raw | ConvertFrom-Json
    foreach ($file in $manifest.Files) {
        if ($file.Path -notmatch '^[A-Za-z0-9_.-]+$|^Assets/launcher\.ico$' -or $file.Path.Contains('..')) { throw 'Небезопасный путь в локальном пакете.' }
        $destination = Join-Path $stage $file.Path
        [IO.Directory]::CreateDirectory((Split-Path $destination)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $payload $file.Path) -Destination $destination
    }
    $helper = Join-Path $stage 'updater-helper.exe'
    Copy-Item -LiteralPath (Join-Path $payload 'Xrmpe.Updater.exe') -Destination $helper
    $job = Join-Path $stage 'job.json'
    @{ Target = $Target; Stage = $stage; ParentPid = 0; ParentStartUtcTicks = 0; Restart = $true } | ConvertTo-Json | Set-Content -LiteralPath $job -Encoding utf8
    & $helper $job
    if ($LASTEXITCODE -ne 0) { throw 'Обновление не выполнено. См. %LOCALAPPDATA%\Xrmpe.CustomLauncher\self-update-result.log. Для Program Files запустите этот CMD от имени администратора либо используйте переносимую папку.' }
    Write-Host 'Готово. Новая версия запущена; настройки сохранены.'
} catch { Write-Error $_; exit 1 }
