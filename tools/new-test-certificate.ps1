<#
.SYNOPSIS
    Makes a self-signed code-signing certificate for trying out signed builds. Nothing trusts it.

.DESCRIPTION
    Writes cashgear-test-signing.pfx and cashgear-test-signing.password.txt to -OutputDir and prints the
    certificate's thumbprint and the signtool arguments to pass to tools\package.ps1. The certificate is made in
    memory: it is never added to a Windows certificate store, so this PC still doesn't trust what it signs (Windows
    and verify-signatures.ps1 show it as signed by an untrusted certificate). Delete the folder when done.

    Real releases need a certificate from a certificate authority or Azure Trusted Signing (docs\SIGNING.md).

.EXAMPLE
    powershell -File tools\new-test-certificate.ps1 -OutputDir $env:TEMP\cashgear-signing
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDir,
    [string]$Subject = "CN=CashGear Capture test signing (not trusted)",
    [int]$Days = 30
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $OutputDir | Out-Null
$OutputDir = (Resolve-Path $OutputDir).Path

$rsa = [System.Security.Cryptography.RSA]::Create(3072)
$request = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
    $Subject, $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
$request.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension($false, $false, 0, $true)))
$request.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
    [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true)))
$usages = New-Object System.Security.Cryptography.OidCollection
$usages.Add((New-Object System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.3", "Code Signing"))) | Out-Null
$request.CertificateExtensions.Add((New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($usages, $false)))

$now = [DateTimeOffset]::Now
$certificate = $request.CreateSelfSigned($now.AddMinutes(-5), $now.AddDays($Days))

$bytes = New-Object byte[] 18
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$password = [Convert]::ToBase64String($bytes) -replace '[+/=]', 'x'

$pfx = Join-Path $OutputDir "cashgear-test-signing.pfx"
[IO.File]::WriteAllBytes($pfx, $certificate.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $password))
[IO.File]::WriteAllText((Join-Path $OutputDir "cashgear-test-signing.password.txt"), $password)

Write-Host "Test certificate: $pfx"
Write-Host "Thumbprint:       $($certificate.Thumbprint)"
Write-Host "Valid until:      $($certificate.NotAfter)"
Write-Host ""
Write-Host "From PowerShell:"
Write-Host ("  .\tools\package.ps1 -SignerThumbprint {0} -SignParams '/fd sha256 /f ""{1}"" /p {2} /tr http://timestamp.digicert.com /td sha256'" -f $certificate.Thumbprint, $pfx, $password)
