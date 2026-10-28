param([string]$Backup = 'backups/redesign-before-20261016-175120')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$backupRoot = (Resolve-Path (Join-Path $projectRoot $Backup)).Path
$sourceRoot = Join-Path $projectRoot 'src/Xrmpe.Launcher'
$oldSourceRoot = Join-Path $backupRoot 'src/Xrmpe.Launcher'
$runtime = Get-ChildItem -LiteralPath $oldSourceRoot -Filter '*.cs' -Recurse -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
foreach ($file in $runtime) {
    $relative = $file.FullName.Substring($oldSourceRoot.Length + 1)
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $sourceRoot $relative)).Hash) { throw "Runtime changed: $relative" }
}
Write-Output "PASS: all $($runtime.Count) original C# source files byte-identical (launch, Steam, IP, server, logging, updates, settings)."
$stable = Join-Path $projectRoot 'artifacts/final-release'
$oldStable = Join-Path $backupRoot 'final-release'
$stableFiles = @(Get-ChildItem -LiteralPath $oldStable -Recurse -File)
if (@(Get-ChildItem -LiteralPath $stable -Recurse -File).Count -ne $stableFiles.Count) { throw 'Stable file count changed' }
foreach ($file in $stableFiles) {
    $relative = $file.FullName.Substring($oldStable.Length + 1)
    if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $stable $relative)).Hash) { throw "Stable release changed: $relative" }
}
Write-Output "PASS: all $($stableFiles.Count) stable final-release files SHA-256 identical to backup."
foreach ($icon in @('launcher.ico', 'launcher-source.png', 'launcher-preview.png')) {
    if ((Get-FileHash -LiteralPath (Join-Path $oldSourceRoot "Assets/$icon")).Hash -ne (Get-FileHash -LiteralPath (Join-Path $sourceRoot "Assets/$icon")).Hash) { throw "Icon changed: $icon" }
}
Write-Output 'PASS: original gasmask icon and source preserved.'
[xml]$oldMarkup = Get-Content -LiteralPath (Join-Path $oldSourceRoot 'MainWindow.axaml') -Raw
[xml]$newMarkup = Get-Content -LiteralPath (Join-Path $sourceRoot 'MainWindow.axaml') -Raw
foreach ($control in $oldMarkup.SelectNodes('//*[@Name]')) {
    $name = $control.GetAttribute('Name')
    $newControl = $newMarkup.SelectSingleNode("//*[@Name='$name']")
    if ($null -eq $newControl) { throw "Control missing: $name" }
    foreach ($attribute in @('Click','SelectionChanged','LostFocus','Minimum','Maximum','Value','SelectedIndex','IsEnabled')) {
        if ($control.HasAttribute($attribute) -and $control.GetAttribute($attribute) -ne $newControl.GetAttribute($attribute)) { throw "Control behavior changed: $name/$attribute" }
    }
}
Write-Output 'PASS: all existing named controls, event handlers, numeric limits, default values and initial enabled states retained.'
