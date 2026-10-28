param([string]$App = 'artifacts/global-browser-preview/app/Xrmpe.Launcher.exe', [string]$Output = 'artifacts/global-browser-preview/overhaul-native-settled-performance.json')
$ErrorActionPreference = 'Stop'
$appPath = [IO.Path]::GetFullPath((Join-Path $PWD $App))
$data = Join-Path $env:LOCALAPPDATA 'Xrmpe.CustomLauncher'
$before = @{}
Get-ChildItem -LiteralPath $data -Filter '*.json' -File | Where-Object Name -ne 'self-update-result.json' | ForEach-Object { $before[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName).Hash }
$info = [Diagnostics.ProcessStartInfo]::new($appPath)
$info.WorkingDirectory = Split-Path $appPath
$info.UseShellExecute = $false
$info.RedirectStandardError = $true
$clock = [Diagnostics.Stopwatch]::StartNew()
$instance = [Diagnostics.Process]::Start($info)
$errors = $instance.StandardError.ReadToEndAsync()
try {
    while (-not $instance.HasExited -and $instance.MainWindowHandle -eq 0 -and $clock.Elapsed.TotalSeconds -lt 25) { Start-Sleep -Milliseconds 100; $instance.Refresh() }
    if ($instance.HasExited -or $instance.MainWindowHandle -eq 0) { throw 'Window did not open' }
    $startup = $clock.Elapsed.TotalMilliseconds
    Start-Sleep -Seconds 20
    $samples = @()
    foreach ($sample in 1..3) {
        $instance.Refresh(); $cpu = $instance.TotalProcessorTime.TotalMilliseconds
        $watch = [Diagnostics.Stopwatch]::StartNew(); Start-Sleep -Seconds 10; $instance.Refresh()
        $samples += [ordered]@{ Seconds=$watch.Elapsed.TotalSeconds; CpuOneCorePercent=($instance.TotalProcessorTime.TotalMilliseconds-$cpu)/$watch.Elapsed.TotalMilliseconds*100; WorkingSetMiB=$instance.WorkingSet64/1MB; PrivateMemoryMiB=$instance.PrivateMemorySize64/1MB }
    }
    if (-not $instance.CloseMainWindow() -or -not $instance.WaitForExit(15000)) { throw 'Normal close failed' }
    $unchanged = @($before.Keys | Where-Object { (Get-FileHash -LiteralPath $_).Hash -ne $before[$_] }).Count -eq 0
    [ordered]@{ NativeWindowStartupMs=$startup; WarmupSeconds=20; Samples=$samples; ExitCode=$instance.ExitCode; Stderr=$errors.GetAwaiter().GetResult(); UserPreferencesUnchanged=$unchanged; GpuMeasured=$false; Scope='Actual preview window; normal startup requests; no game/server launched. Three settled 10-second samples, CPU relative to one core.' } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Output
    Get-Content -LiteralPath $Output
} finally { if (-not $instance.HasExited) { [void]$instance.CloseMainWindow(); [void]$instance.WaitForExit(15000) }; $instance.Dispose() }
