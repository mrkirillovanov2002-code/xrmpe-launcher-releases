param([Parameter(Mandatory=$true)][string]$PublishPath)
$ErrorActionPreference = 'Stop'
$publish = [IO.Path]::GetFullPath($PublishPath)
$source = Join-Path $PSScriptRoot 'ThirdPartyLicenses'
$deps = Get-Content -LiteralPath (Join-Path $publish 'Xrmpe.Launcher.deps.json') -Raw | ConvertFrom-Json
$cache = Join-Path $env:USERPROFILE '.nuget/packages'
$candidates = @{}
$packages = @()
$approved = Get-Content -LiteralPath (Join-Path $source 'APPROVED-PACKAGES.json') -Raw | ConvertFrom-Json
foreach ($library in $deps.libraries.PSObject.Properties) {
    if ($library.Value.type -eq 'project') { continue }
    $id = $library.Name -replace '^runtimepack\.', ''
    $folder = Join-Path $cache $id.ToLowerInvariant()
    if (-not (Test-Path -LiteralPath $folder)) { throw "Missing package for provenance: $id" }
    $spec = Get-ChildItem -LiteralPath $folder -Filter '*.nuspec' | Select-Object -First 1
    [xml]$xml = Get-Content -LiteralPath $spec.FullName
    $metadata = $xml.package.metadata
    $license = [string]$metadata.license.'#text'
    if ($metadata.license.type -eq 'file' -and $metadata.id -eq 'Avalonia.Angle.Windows.Natives') { $license = 'BSD-3-Clause' }
    if ($license -notin @('MIT', 'BSD-3-Clause') -or $approved.$id -ne $license) { throw "Unreviewed package/version/license for $id : $license" }
    $packages += [ordered]@{ Package=$id; License=$license; Copyright=[string]$metadata.copyright; Repository=[string]$metadata.repository.url }
    foreach ($file in Get-ChildItem -LiteralPath $folder -Recurse -File | Where-Object Extension -in '.dll','.exe') {
        if (-not $candidates.ContainsKey($file.Name)) { $candidates[$file.Name] = [Collections.Generic.List[object]]::new() }
        $candidates[$file.Name].Add(@{ Path=$file.FullName; Package=$id; License=$license })
    }
}
$own = @('Xrmpe.Launcher.exe','Xrmpe.Launcher.dll','Xrmpe.SelfUpdate.dll','Xrmpe.Updater.exe','Xrmpe.Launcher.deps.json','Xrmpe.Launcher.runtimeconfig.json','Assets/launcher.ico')
$allowed = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/Xrmpe.SelfUpdate/AllowedFiles.txt')
$inventory = @()
foreach ($name in $allowed) {
    if ($name -in @('LICENSE.txt','THIRD-PARTY-NOTICES.txt','THIRD-PARTY-COMPONENTS.json')) { continue }
    $path = Join-Path $publish $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($name -in $own) {
        $inventory += [ordered]@{ Path=$name; Sha256=$hash; Origin= $(if($name -eq 'Xrmpe.Launcher.dll'){'ZoneLink launcher and embedded local builder (CPython 3.12, NumPy 2.3.5, Pillow 12.3.0)'}else{'xrMPE Custom Launcher build'}); License= $(if($name -eq 'Xrmpe.Launcher.dll'){'Project scripts; CPython PSF; NumPy BSD-3-Clause; Pillow MIT-CMU; bundled notices'}else{'Project component; bundled .NET notices apply to apphost and single-file Updater'}) }
        continue
    }
    $match = $null
    foreach ($candidate in $candidates[[IO.Path]::GetFileName($name)]) {
        if ((Get-FileHash -LiteralPath $candidate.Path -Algorithm SHA256).Hash -eq $hash) { $match=$candidate; break }
    }
    if ($null -eq $match) { throw "No verified NuGet origin for distributed file: $name" }
    $inventory += [ordered]@{ Path=$name; Sha256=$hash; Origin=$match.Package; License=$match.License }
}
$licenseNames = @('dotnet-runtime-LICENSE.txt','Avalonia-LICENSE.txt','ANGLE-LICENSE.txt','SkiaSharp-LICENSE.txt','HarfBuzzSharp-LICENSE.txt','MicroCom-LICENSE.txt','Tmds.DBus-LICENSE.txt','System.IO.Pipelines-LICENSE.txt','Apache-2.0.txt','CPython-LICENSE.txt','NumPy-LICENSE.txt','Pillow-LICENSE.txt')
$noticeNames = @('dotnet-runtime-NOTICES.txt','aspnetcore-NOTICES.txt','Avalonia-NOTICES.txt','SkiaSharp-HarfBuzzSharp-NOTICES.txt','System.IO.Pipelines-NOTICES.txt')
$expected = Get-Content -LiteralPath (Join-Path $source 'SHA256SUMS.json') -Raw | ConvertFrom-Json
foreach ($name in $licenseNames + $noticeNames) {
    if ((Get-FileHash -LiteralPath (Join-Path $source $name)).Hash -ne $expected.$name) { throw "Upstream notice changed without review: $name" }
}
$header = @'
xrMPE Custom Launcher - third-party license collection

The licenses below apply to their named third-party components, not to the
launcher as a whole. This file does not grant rights to game files or trademarks.
No game files are included. Component versions and binary provenance are listed
in THIRD-PARTY-COMPONENTS.json. Full upstream third-party attributions and nested
license terms are preserved in THIRD-PARTY-NOTICES.txt without translation.
The single-file Xrmpe.Updater.exe and launcher apphost include .NET components.
System fonts are supplied by Windows; no font files are redistributed here.
Original launcher icon and background are project-generated artwork.
The launcher embeds CPython, NumPy and Pillow for on-device reconstruction from
the user's installed xrMPE. Their license texts follow below. Modified game
OGF/DDS/DB files are generated locally and are not in the public package.

'@
$licenses = $header
foreach ($name in $licenseNames) { $licenses += "`n========== $name ==========`n" + [IO.File]::ReadAllText((Join-Path $source $name)) + "`n" }
[IO.File]::WriteAllText((Join-Path $publish 'LICENSE.txt'), $licenses, [Text.UTF8Encoding]::new($false))
$notices = "THIRD-PARTY SOFTWARE NOTICES`nFull upstream notices; some entries cover platform variants not shipped in win-x64.`n"
foreach ($name in $noticeNames) { $notices += "`n========== $name ==========`n" + [IO.File]::ReadAllText((Join-Path $source $name)) + "`n" }
[IO.File]::WriteAllText((Join-Path $publish 'THIRD-PARTY-NOTICES.txt'), $notices, [Text.UTF8Encoding]::new($false))
[ordered]@{ Schema=1; Packages=$packages; Files=$inventory; Note='Packages are the complete dependency graph; Files identify the exact distributed binaries. Nested licenses/attributions are retained in LICENSE.txt and THIRD-PARTY-NOTICES.txt.' } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $publish 'THIRD-PARTY-COMPONENTS.json') -Encoding utf8
Write-Output "License provenance verified: $($inventory.Count) files; complete upstream notices included."
