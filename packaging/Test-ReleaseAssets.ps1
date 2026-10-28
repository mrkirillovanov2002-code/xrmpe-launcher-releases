param([Parameter(Mandatory=$true)][string]$ReleaseRoot, [string]$Version='0.5.0', [string]$Repository='mrkirillovanov2002-code/xrmpe-launcher-releases')
$ErrorActionPreference='Stop'
$assets=Join-Path $ReleaseRoot 'ReleaseAssets'
$expected=@('xrMPE-Launcher-win-x64.zip','launcher-github.json','launcher-github.sig')
$actual=@(Get-ChildItem -LiteralPath $assets -File)
if ($actual.Count -ne 3 -or @($actual.Name | Where-Object { $_ -notin $expected }).Count -ne 0) { throw 'ReleaseAssets must contain exactly ZIP, JSON, SIG' }
$json=[IO.File]::ReadAllBytes((Join-Path $assets 'launcher-github.json'))
$sig=[IO.File]::ReadAllBytes((Join-Path $assets 'launcher-github.sig'))
$rsa=[Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([IO.File]::ReadAllText((Join-Path $PSScriptRoot '../src/Xrmpe.SelfUpdate/ReleasePublicKey.pem')))
    if (-not $rsa.VerifyData($json,$sig,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)) { throw 'Invalid RSA-PSS signature' }
    $damaged=$json.Clone();$damaged[10]=$damaged[10] -bxor 1
    if ($rsa.VerifyData($damaged,$sig,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)) { throw 'Tampered manifest accepted' }
} finally { $rsa.Dispose() }
$manifest=[Text.Encoding]::UTF8.GetString($json)|ConvertFrom-Json
if ($manifest.Release.Version -ne $Version -or $manifest.Tag -ne ('v'+$Version) -or $manifest.Repository -ne $Repository) { throw 'Wrong release identity' }
$zipPath=Join-Path $assets $manifest.Archive.Name
if ((Get-Item -LiteralPath $zipPath).Length -ne $manifest.Archive.Size -or (Get-FileHash -LiteralPath $zipPath).Hash -ne $manifest.Archive.Sha256) { throw 'Archive hash/size mismatch' }
$allowed=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/Xrmpe.SelfUpdate/AllowedFiles.txt')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    if ($zip.Entries.Count -ne $manifest.Release.Files.Count) { throw 'Archive/manifest entry count mismatch' }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $texts=@{}
    foreach ($entry in $zip.Entries) {
        $name=$entry.FullName
        if ($name -notin $allowed -or -not $seen.Add($name) -or $name.Contains('..') -or $name.Contains('\') -or $name.Contains(':') -or $name.StartsWith('/') -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw "Unsafe ZIP entry: $name" }
        $item=@($manifest.Release.Files|Where-Object Path -eq $name)
        if ($item.Count -ne 1 -or $entry.Length -ne $item[0].Size) { throw "Unexpected file: $name" }
        $stream=$entry.Open();$memory=[IO.MemoryStream]::new()
        try { $stream.CopyTo($memory);$bytes=$memory.ToArray() } finally { $stream.Dispose();$memory.Dispose() }
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)) -ne $item[0].Sha256) { throw "Bad file hash: $name" }
        foreach($enc in @([Text.Encoding]::UTF8,[Text.Encoding]::Unicode)) {
            $text=$enc.GetString($bytes)
            if ($text -match '-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{50,}|C:\\Users\\Кирилл|C:\\Games\\STALKER\\X-Ray Multiplayer Extension\\game') { throw "Sensitive content pattern in $name" }
        }
        if ($name -in @('LICENSE.txt','THIRD-PARTY-NOTICES.txt','THIRD-PARTY-COMPONENTS.json')) { $texts[$name]=[Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF) }
    }
    foreach($name in @('LICENSE.txt','THIRD-PARTY-NOTICES.txt','THIRD-PARTY-COMPONENTS.json')) { if (-not $texts.ContainsKey($name)) { throw "Missing document: $name" } }
    foreach($original in Get-ChildItem (Join-Path $PSScriptRoot 'ThirdPartyLicenses') -Filter '*.txt') {
        $content=[IO.File]::ReadAllText($original.FullName)
        if (-not ($texts['LICENSE.txt'].Contains($content) -or $texts['THIRD-PARTY-NOTICES.txt'].Contains($content))) { throw "Incomplete original notice: $($original.Name)" }
    }
    $inventory=$texts['THIRD-PARTY-COMPONENTS.json']|ConvertFrom-Json
    foreach($file in $inventory.Files) {
        $signed=@($manifest.Release.Files|Where-Object Path -eq $file.Path)
        if ($signed.Count -ne 1 -or $signed[0].Sha256 -ne $file.Sha256) { throw "Invalid provenance hash: $($file.Path)" }
    }
    Write-Output "PASS: $($zip.Entries.Count) ZIP files, exact signed hashes/sizes; all 14 upstream license/notice originals preserved; provenance inventory matches payload; no sensitive patterns."
} finally { $zip.Dispose() }
Write-Output ('PASS: publisher RSA-PSS signature; modified manifest rejected; v'+$Version+' repository/tag verified.')
Write-Output ('Download base (not yet published): https://github.com/' + $manifest.Repository + '/releases/download/' + $manifest.Tag + '/')
Get-ChildItem -LiteralPath $assets -File | ForEach-Object { Write-Output ((Get-FileHash -LiteralPath $_.FullName).Hash + '  ' + $_.Name + '  ' + $_.Length) }
