param([string]$KeyName = 'xrMPE.Launcher.ReleaseSigning')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not [Security.Cryptography.CngKey]::Exists($KeyName)) {
    $options = [Security.Cryptography.CngKeyCreationParameters]::new()
    $options.Provider = [Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
    $options.KeyUsage = [Security.Cryptography.CngKeyUsages]::Signing
    $options.ExportPolicy = [Security.Cryptography.CngExportPolicies]::None
    $options.Parameters.Add([Security.Cryptography.CngProperty]::new('Length', [BitConverter]::GetBytes(3072), [Security.Cryptography.CngPropertyOptions]::None))
    $createdKey = [Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::Rsa, $KeyName, $options)
    $createdKey.Dispose()
}
$signingKey = [Security.Cryptography.CngKey]::Open($KeyName)
$rsa = [Security.Cryptography.RSACng]::new($signingKey)
try {
    $publicPath = Join-Path $projectRoot 'src\Xrmpe.SelfUpdate\ReleasePublicKey.pem'
    $publicPem = $rsa.ExportSubjectPublicKeyInfoPem()
    if ((Test-Path -LiteralPath $publicPath) -and (Get-Content -LiteralPath $publicPath -Raw).Trim() -ne $publicPem.Trim()) {
        throw 'Existing public key differs. Key rotation requires a separately reviewed migration; public key was not overwritten.'
    }
    Set-Content -LiteralPath $publicPath -Value $publicPem -Encoding ascii
    Write-Output ('Public key: ' + $publicPath)
    Write-Output ('Publisher CNG key: ' + $KeyName + ' (current Windows user; private key is not exported)')
    Write-Output ('Public SHA-256: ' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rsa.ExportSubjectPublicKeyInfo())))
} finally { $rsa.Dispose(); $signingKey.Dispose() }
