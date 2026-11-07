# Makes TokenOverlay's own code-signing certificate, once. Its private key stays in this Windows user's certificate
# store (Cert:\CurrentUser\My); only the thumbprint (scripts/signing-cert.txt) and the public certificate
# (publish/signing/TokenOverlay-signing.cer, for the laptop's App Control policy) are written out. The certificate is
# not added to any trust store on this PC. For TokenOverlay only: never sign another project with it.
#   pwsh scripts/new-signing-cert.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$thumbFile = Join-Path $PSScriptRoot 'signing-cert.txt'
$publicDir = Join-Path $root 'publish\signing'
$subject = 'CN=TokenOverlay Code Signing (igayowai)'

if (Test-Path $thumbFile) { throw "a signing certificate already exists ($(Get-Content $thumbFile)); it is reused, never made again" }
if (Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq $subject) { throw "a certificate named $subject is already in the store" }

# Exportable only so that one password-protected offline backup can be made (scripts/backup-signing-key.ps1).
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject -FriendlyName 'TokenOverlay code signing' `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable -KeyUsage DigitalSignature `
    -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(10)
Set-Content -Path $thumbFile -Value $cert.Thumbprint -NoNewline
New-Item -ItemType Directory $publicDir -Force | Out-Null
Export-Certificate -Cert $cert -FilePath (Join-Path $publicDir 'TokenOverlay-signing.cer') -Type CERT | Out-Null
"made $subject · thumbprint $($cert.Thumbprint) · valid until $($cert.NotAfter.ToString('yyyy-MM-dd'))"
