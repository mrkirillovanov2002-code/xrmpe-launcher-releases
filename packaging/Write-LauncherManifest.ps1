param([Parameter(Mandatory=$true)][string]$Directory, [Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$files = @()
foreach ($name in (Get-Content -LiteralPath (Join-Path $root 'src/Xrmpe.SelfUpdate/AllowedFiles.txt'))) {
    $path = Join-Path $Directory $name
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $files += [ordered]@{ Path=$name; Size=(Get-Item -LiteralPath $path).Length; Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    }
}
@{Schema=1; Version=$Version; Files=@($files)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Directory 'launcher-release.json') -Encoding utf8
