param(
    [Parameter(Mandatory=$true)][string]$Repository,
    [string]$Version = '0.5.0',
    [string]$Output = '',
    [string]$Notes = '',
    [string]$KeyName = 'xrMPE.Launcher.ReleaseSigning'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if ($Repository -eq 'OWNER/REPOSITORY') { throw 'Specify the real public repository before preparing publication assets.' }
if (-not $Output) { $Output = 'artifacts/github-release-' + $Version + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') }
$outputPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Output))
if (Test-Path -LiteralPath $outputPath) { throw ('Output exists: ' + $outputPath) }
if (-not $Notes) { $Notes = Join-Path $projectRoot 'docs/GITHUB-RELEASE-NOTES-RU.md' }
$payloadRelative = $Output.TrimEnd('/','\') + '/app'
& (Join-Path $PSScriptRoot 'Publish-SelfUpdate.ps1') -Version $Version -Output $payloadRelative
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
& dotnet run --project (Join-Path $projectRoot 'tools/Xrmpe.ReleasePack') -c Release -- (Join-Path $outputPath 'app') (Join-Path $outputPath 'ReleaseAssets') $Repository $Notes $KeyName
if ($LASTEXITCODE -ne 0) { throw 'Signing/package failed' }
Write-Output 'Assets prepared locally. Nothing was uploaded. After reviewing them, create a GitHub Release and attach ONLY the three files in ReleaseAssets: archive, JSON, signature.'
Write-Output ('Release assets: ' + (Join-Path $outputPath 'ReleaseAssets'))
