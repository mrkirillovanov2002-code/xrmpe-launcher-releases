param([string]$Version = '0.3.1', [string]$Output = 'artifacts/self-update-preview/Release-0.3.1')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$outputPath = [IO.Path]::GetFullPath((Join-Path $root $Output))
if (Test-Path -LiteralPath $outputPath) { throw "Output already exists: $outputPath. Use a new folder." }
$helperOutput = $outputPath + '-updater-build'
& dotnet publish (Join-Path $root 'src/Xrmpe.Updater') -c Release -r win-x64 --self-contained true -o $helperOutput
if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed' }
& dotnet publish (Join-Path $root 'src/Xrmpe.Launcher') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -o $outputPath
if ($LASTEXITCODE -ne 0) { throw 'Launcher publish failed' }
Copy-Item -LiteralPath (Join-Path $helperOutput 'Xrmpe.Updater.exe') -Destination $outputPath
& (Join-Path $PSScriptRoot 'Write-ThirdPartyNotices.ps1') -PublishPath $outputPath
$allowed = Get-Content -LiteralPath (Join-Path $root 'src/Xrmpe.SelfUpdate/AllowedFiles.txt')
$files = @()
foreach ($name in $allowed) {
    $path = Join-Path $outputPath $name
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $files += [ordered]@{ Path = $name; Size = (Get-Item -LiteralPath $path).Length; Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    }
}
@{ Schema = 1; Version = $Version; Files = @($files) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputPath 'launcher-release.json') -Encoding utf8
Write-Output "Published $Version : $outputPath ($($files.Count) allowed files)"
