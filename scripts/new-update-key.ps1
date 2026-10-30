<#
.SYNOPSIS
    Makes Granary's update signing key, once (docs/release.md, "Signed releases").

.DESCRIPTION
    Generates a P-256 key pair in memory, with nothing stored in Windows' key store. The private key goes straight into
    the UPDATE_SIGNING_KEY secret of the repository's "release" environment through `gh secret set`, read from standard
    input: it is never written to disk, printed or put on a command line. Only once that worked is the public key
    written to src/Granary.Core/Updates/update-key.pub, which the app carries. Commit that file.

    Run it once. Installed copies carry the public key they were built with and refuse releases signed with any
    other key, so a new key later means their users must install the next release by hand. -Replace is for that
    case only: a key that was lost or leaked.

    Needs the GitHub CLI (gh), signed in with admin rights on the repository, and the "release" environment in place.
    Run it on a machine without PowerShell logging policies (module logging, script block logging, transcription): they
    could keep what the script handles. It stops when such a policy is on, but it cannot see a Start-Transcript started
    by hand or by a profile, so run it in a fresh window without a profile, as below.

.EXAMPLE
    powershell -NoProfile -File ./scripts/new-update-key.ps1 -Repository owner/granary
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('\A[A-Za-z0-9-]+/[A-Za-z0-9._-]+\z')]
    [string]$Repository,
    # Replaces a key update-key.pub already holds. Installed copies then refuse every later release (see above).
    [switch]$Replace
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'lib/update-signing.ps1')

$keyPath = $script:UpdateKeyPath
if (-not ([IO.File]::ReadAllText($keyPath) -match 'PLACEHOLDER') -and -not $Replace) {
    throw "$keyPath already holds the update signing key. A new one makes installed copies refuse every later release; pass -Replace only when the key was lost or leaked (docs/release.md)."
}
$logging = Get-PowerShellLoggingPolicy
if ($logging.Count -gt 0) {
    throw "PowerShell logging is on here ($($logging -join ', ')), and it could keep the private key. Run this on a machine without it (docs/release.md)."
}
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'This needs the GitHub CLI (gh), signed in with admin rights on the repository.'
}
gh api "repos/$Repository/environments/release" --silent
if ($LASTEXITCODE -ne 0) {
    throw "$Repository has no ""release"" environment that gh can see. Create it first (docs/release.md, 'Before the first public release')."
}

$key = New-EphemeralP256Key
try {
    $publicBlob = $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    $publicPem = ConvertTo-Pem -Der (ConvertTo-P256Spki -EccPublicBlob $publicBlob) -Label 'PUBLIC KEY'
    $der = $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
    try {
        # Into the secret through standard input: never on disk, on screen or on a command line.
        ConvertTo-Pem -Der $der -Label 'PRIVATE KEY' | gh secret set UPDATE_SIGNING_KEY --env release --repo $Repository
        if ($LASTEXITCODE -ne 0) {
            throw "gh secret set failed. The key was not stored, and $keyPath is unchanged."
        }
    }
    finally {
        [Array]::Clear($der, 0, $der.Length)
    }
}
finally {
    $key.Dispose()
}

[IO.File]::WriteAllText($keyPath, $publicPem, [Text.UTF8Encoding]::new($false))
$null = Read-UpdatePublicKey -Path $keyPath
Write-Host "UPDATE_SIGNING_KEY is set in the ""release"" environment of $Repository, and the public key is in $keyPath."
Write-Host 'Commit that file; releases built from then on are signed, and copies built with it accept only those.'
