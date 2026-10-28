# Test-signs the package (driver and catalog) with a self-signed "wfsa Test Signing" certificate.
# The private key stays in this user's certificate store; only the public .cer is written next to the package, to be trusted on the
# TEST VM by install.ps1. This does not change this machine's trust: nothing is added to the Root or TrustedPublisher stores here.
# -Remove deletes the certificate (and its key) from this user's store again.
param([switch]$Remove)
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pkg = Join-Path $root 'build\package'
$subject = 'CN=wfsa Test Signing (not for production)'

if ($Remove) {
    Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject } | ForEach-Object { Remove-Item $_.PSPath -DeleteKey; Write-Host "Removed $($_.Thumbprint)" }
    return
}

foreach ($f in 'AuditorDriver.sys', 'AuditorDriver.cat') {
    if (-not (Test-Path (Join-Path $pkg $f))) { throw "$f is missing. Run scripts\package.ps1 first." }
}

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(2) -CertStoreLocation Cert:\CurrentUser\My
    Write-Host "Created test certificate $($cert.Thumbprint)"
}
Export-Certificate -Cert $cert -FilePath (Join-Path $pkg 'wfsa-test-signing.cer') | Out-Null

$signtool = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe'
if (-not (Test-Path $signtool)) { throw "signtool not found at $signtool" }

foreach ($f in 'AuditorDriver.sys', 'AuditorDriver.cat') {
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My "$(Join-Path $pkg $f)"
    if ($LASTEXITCODE -ne 0) { throw "signing $f failed" }
}

foreach ($f in 'AuditorDriver.sys', 'AuditorDriver.cat') {
    $sig = Get-AuthenticodeSignature (Join-Path $pkg $f)
    Write-Host ('  {0,-20} signer={1}  status={2}' -f $f, $sig.SignerCertificate.Subject, $sig.Status)
}
Write-Host 'The status is UnknownError/NotTrusted here by design: the certificate is only trusted on the test VM.'
