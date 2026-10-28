param(
    [Parameter(Mandatory=$true)][string]$PreviousRelease,
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$Repository = 'mrkirillovanov2002-code/xrmpe-launcher-releases'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$previous = [IO.Path]::GetFullPath((Join-Path $root $PreviousRelease))
$outputPath = [IO.Path]::GetFullPath((Join-Path $root $Output))
if (Test-Path -LiteralPath $outputPath) { throw "Output already exists: $outputPath" }

# Reuse only bytes from a locally archived, signed, verified prior release.
$global:LASTEXITCODE = 0
& (Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1') -ReleaseRoot $previous -Version '0.6.1.2' -Repository $Repository | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Previous release verification failed.' }
$oldArchive = Join-Path $previous 'ReleaseAssets\xrMPE-Launcher-win-x64.zip'
$app = Join-Path $outputPath 'app'
New-Item -ItemType Directory -Path $app -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($oldArchive, $app)

$built = Join-Path $root 'src\Xrmpe.Launcher\bin\Release\net8.0'
foreach ($name in @('Xrmpe.Launcher.exe', 'Xrmpe.Launcher.dll', 'Xrmpe.SelfUpdate.dll')) {
    $source = Join-Path $built $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Release build missing: $name" }
    Copy-Item -LiteralPath $source -Destination (Join-Path $app $name) -Force
}
if ((Get-Item (Join-Path $app 'Xrmpe.Launcher.dll')).VersionInfo.FileVersion -ne '0.6.1.3') {
    throw 'Launcher DLL is not v0.6.1.3.'
}

# Preserve the signed prior release's self-contained .NET 8.0.8 runtime graph.
# The only dependency identity changed is our own launcher assembly version.
$depsPath = Join-Path $app 'Xrmpe.Launcher.deps.json'
$deps = [IO.File]::ReadAllText($depsPath)
$oldIdentity = '"Xrmpe.Launcher/0.6.1"'
$occurrences = ([regex]::Matches($deps, [regex]::Escape($oldIdentity))).Count
if ($occurrences -ne 2) { throw "Unexpected previous dependency identity count: $occurrences" }
$deps = $deps.Replace($oldIdentity, '"Xrmpe.Launcher/0.6.1.3"')
[IO.File]::WriteAllText($depsPath, $deps, [Text.UTF8Encoding]::new($false))

$global:LASTEXITCODE = 0
& (Join-Path $PSScriptRoot 'Write-ThirdPartyNotices.ps1') -PublishPath $app
if ($LASTEXITCODE -ne 0) { throw 'License provenance failed.' }
$allowed = Get-Content -LiteralPath (Join-Path $root 'src\Xrmpe.SelfUpdate\AllowedFiles.txt')
$files = @()
foreach ($name in $allowed) {
    $path = Join-Path $app $name
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $files += [ordered]@{ Path=$name; Size=(Get-Item -LiteralPath $path).Length; Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    }
}
@{ Schema=1; Version='0.6.1.3'; Files=@($files) } | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $app 'launcher-release.json') -Encoding utf8

$env:MSBuildEnableWorkloadResolver = 'false'
& dotnet run --project (Join-Path $root 'tools\Xrmpe.ReleasePack') -c Release --no-restore -- $app (Join-Path $outputPath 'ReleaseAssets') $Repository (Join-Path $root 'docs\GITHUB-RELEASE-NOTES-0613-RU.md')
if ($LASTEXITCODE -ne 0) { throw 'Signing/package failed.' }
$global:LASTEXITCODE = 0
& (Join-Path $PSScriptRoot 'Test-ReleaseAssets.ps1') -ReleaseRoot $outputPath -Version '0.6.1.3' -Repository $Repository | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'New release verification failed.' }
Write-Output "Prepared v0.6.1.3 locally at $outputPath. Nothing was uploaded."
