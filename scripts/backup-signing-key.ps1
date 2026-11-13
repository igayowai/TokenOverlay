# One offline backup of TokenOverlay's signing key: a password-protected .pfx written where you choose (a USB stick,
# not a synced or shared folder). Run it yourself in a terminal; it asks for the folder and the password and never
# shows or stores the password.
#   pwsh scripts/backup-signing-key.ps1
$ErrorActionPreference = 'Stop'
$thumb = (Get-Content (Join-Path $PSScriptRoot 'signing-cert.txt')).Trim()
$cert = Get-Item "Cert:\CurrentUser\My\$thumb"
$folder = Read-Host '백업을 둘 폴더 (예: E:\)'
if (-not (Test-Path -LiteralPath $folder -PathType Container)) { throw "폴더가 없음: $folder" }
$password = Read-Host '백업 암호 (화면에 안 보임)' -AsSecureString
$again = Read-Host '같은 암호 한 번 더' -AsSecureString
if ([Net.NetworkCredential]::new('', $password).Password -ne [Net.NetworkCredential]::new('', $again).Password) { throw '두 암호가 다름' }
$target = Join-Path $folder 'TokenOverlay-signing-backup.pfx'
Export-PfxCertificate -Cert $cert -FilePath $target -Password $password -CryptoAlgorithmOption AES256_SHA256 | Out-Null
"백업함: $target · 암호는 따로 적어 두고, 이 파일은 저장소·공유 폴더에 두지 말 것"
