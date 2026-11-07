# Signing for release.ps1 (dot-sourced). Velopack signs through signtool with TokenOverlay's own certificate, found
# in this user's store by the thumbprint in signing-cert.txt (scripts/new-signing-cert.ps1 made it once). After packing,
# every program file that ships is checked, and a missing or broken signature stops the release.

$script:SigningThumbprint = (Get-Content (Join-Path $PSScriptRoot 'signing-cert.txt') -ErrorAction Stop).Trim()

function Get-SigningCertificate {
    $cert = Get-Item "Cert:\CurrentUser\My\$script:SigningThumbprint" -ErrorAction SilentlyContinue
    if (-not $cert) { throw "signing certificate $script:SigningThumbprint is not in this user's store" }
    if (-not $cert.HasPrivateKey) { throw 'signing certificate has no private key here' }
    if ($cert.NotAfter -lt (Get-Date).AddDays(30)) { throw "signing certificate ends $($cert.NotAfter.ToString('yyyy-MM-dd')): replace it first (laptop policy before signing)" }
    $cert
}

# signtool arguments for vpk --signParams: this certificate, SHA-256, and a timestamp so a signature outlives it.
function Get-SignParams {
    $null = Get-SigningCertificate
    "/sha1 $script:SigningThumbprint /s My /fd sha256 /td sha256 /tr http://timestamp.digicert.com"
}

# Fine when the file is signed by our certificate (the chain is checked against that certificate alone, as this PC
# does not trust it) or carries a signature Windows trusts (Microsoft's .NET files). Returns a problem, or $null.
function Test-FileSignature([string]$path) {
    $sig = Get-AuthenticodeSignature -LiteralPath $path
    if ($sig.Status -eq 'NotSigned') { return 'not signed' }
    if ($sig.Status -eq 'HashMismatch') { return 'signature does not match the file' }
    if ($null -eq $sig.SignerCertificate) { return "signature unreadable ($($sig.Status))" }
    if ($sig.SignerCertificate.Thumbprint -eq $script:SigningThumbprint) {
        if ($null -eq $sig.TimeStamperCertificate) { return 'our signature has no timestamp' }
        $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
        $chain.ChainPolicy.TrustMode = 'CustomRootTrust'
        $chain.ChainPolicy.RevocationMode = 'NoCheck'
        $null = $chain.ChainPolicy.CustomTrustStore.Add((Get-SigningCertificate))
        if (-not $chain.Build($sig.SignerCertificate)) { return "our certificate does not verify ($($chain.ChainStatus.Status -join ', '))" }
        return $null
    }
    if ($sig.Status -eq 'Valid') { return $null }
    "signed by $($sig.SignerCertificate.Subject) but not valid ($($sig.Status))"
}

function Test-ProgramFiles([string]$folder, [string]$label) {
    $problems = @()
    Get-ChildItem -LiteralPath $folder -Recurse -File | Where-Object Extension -in '.exe', '.dll', '.node', '.sys' | ForEach-Object {
        if ($problem = Test-FileSignature $_.FullName) { $problems += "$label/$($_.FullName.Substring($folder.Length).TrimStart('\')): $problem" }
    }
    $problems
}

# The setup file, every program file in the full package and the portable zip. Throws listing each problem.
function Assert-ReleaseSigned([string]$releases, [string]$version, [string]$channel) {
    $problems = @()
    $setup = Join-Path $releases "TokenOverlay-$channel-Setup.exe"
    if (Test-Path $setup) { if ($p = Test-FileSignature $setup) { $problems += "Setup.exe: $p" } } else { $problems += 'Setup.exe missing' }
    $scratch = Join-Path ([IO.Path]::GetTempPath()) "TokenOverlay-signcheck-$([guid]::NewGuid().ToString('N'))"
    try {
        # The default channel's package has no channel in its name.
        $full = @("TokenOverlay-$version-$channel-full.nupkg", "TokenOverlay-$version-full.nupkg") |
            ForEach-Object { Join-Path $releases $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $full) { $problems += 'full package missing' }
        else {
            Expand-Archive -LiteralPath $full -DestinationPath "$scratch\full"
            $programs = @(Get-ChildItem "$scratch\full" -Recurse -File | Where-Object Extension -in '.exe', '.dll')
            if (-not ($programs | Where-Object Name -eq 'TokenOverlay.exe')) { $problems += 'full package has no TokenOverlay.exe' }
            $problems += Test-ProgramFiles "$scratch\full" 'package'
        }
        $portable = Join-Path $releases "TokenOverlay-$channel-Portable.zip"
        if (Test-Path $portable) {
            Expand-Archive -LiteralPath $portable -DestinationPath "$scratch\portable"
            $problems += Test-ProgramFiles "$scratch\portable" 'portable'
        }
    }
    finally { Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue }
    if ($problems) { throw "signature check failed:`n  " + ($problems -join "`n  ") }
    "signature check passed: setup, package and portable files"
}
