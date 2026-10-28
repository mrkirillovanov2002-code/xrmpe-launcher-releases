param(
    [Parameter(Mandatory=$true)][string]$PythonHome,
    [Parameter(Mandatory=$true)][string]$Destination
)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'LocalEmissiveBuilder'
$runtime = Join-Path $Destination 'runtime'
New-Item -ItemType Directory -Force -Path $Destination,$runtime | Out-Null
foreach ($name in @('build_from_install.py','build_local.py','build_starter.py','inspect_geometry.py','read_db.py','render_previews.py','costumes.json')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $Destination $name) -Force
}
foreach ($name in @('python.exe','python3.dll','python312.dll','vcruntime140.dll','vcruntime140_1.dll','LICENSE.txt')) {
    Copy-Item -LiteralPath (Join-Path $PythonHome $name) -Destination (Join-Path $runtime $name) -Force
}
Copy-Item -LiteralPath (Join-Path $PythonHome 'DLLs') -Destination (Join-Path $runtime 'DLLs') -Recurse -Force
$sourceLib = Join-Path $PythonHome 'Lib'
$targetLib = Join-Path $runtime 'Lib'
New-Item -ItemType Directory -Force -Path $targetLib | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $sourceLib) {
    if ($item.Name -in @('site-packages','__pycache__','test','idlelib','tkinter','turtledemo')) { continue }
    Copy-Item -LiteralPath $item.FullName -Destination (Join-Path $targetLib $item.Name) -Recurse -Force
}
$site = Join-Path $targetLib 'site-packages'
New-Item -ItemType Directory -Force -Path $site | Out-Null
foreach ($name in @('numpy','numpy.libs','numpy-2.3.5.dist-info','PIL','pillow-12.3.0.dist-info')) {
    Copy-Item -LiteralPath (Join-Path $sourceLib "site-packages\$name") -Destination (Join-Path $site $name) -Recurse -Force
}
$destinationRoot = [IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
foreach ($cache in Get-ChildItem -LiteralPath $Destination -Recurse -Directory -Filter '__pycache__') {
    $resolved = [IO.Path]::GetFullPath($cache.FullName)
    if (-not $resolved.StartsWith($destinationRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to remove directory outside builder: $resolved" }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if (-not (Test-Path -LiteralPath (Join-Path $site 'numpy-2.3.5.dist-info\LICENSE.txt'))) { throw 'NumPy license missing' }
if (-not (Test-Path -LiteralPath (Join-Path $site 'pillow-12.3.0.dist-info\licenses\LICENSE'))) { throw 'Pillow license missing' }
Write-Output "Local builder staged: $Destination"
