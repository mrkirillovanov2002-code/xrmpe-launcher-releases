[CmdletBinding()]
param([string]$ISCCPath, [string]$Output = 'artifacts/installer-upgrade-fix-0.3.1')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ISCCPath)) {
    $compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($compiler) { $ISCCPath = $compiler.Source }
}
if ([string]::IsNullOrWhiteSpace($ISCCPath)) {
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
        if (-not $base) { continue }
        $candidate = Join-Path $base 'Inno Setup 6\ISCC.exe'
        if (Test-Path -LiteralPath $candidate) { $ISCCPath = $candidate; break }
    }
}
if ([string]::IsNullOrWhiteSpace($ISCCPath) -or -not (Test-Path -LiteralPath $ISCCPath)) {
    throw 'ISCC.exe unavailable. No installer was built.'
}
# The legacy entry point now uses the same complete publication (including updater),
# includes and version as the release builder. Never overwrite installer-preview/app.
& (Join-Path $PSScriptRoot 'Build-Release031Installer.ps1') -ISCCPath $ISCCPath -Output $Output
