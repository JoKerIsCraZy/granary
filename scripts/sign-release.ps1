<#
.SYNOPSIS
    Signs a packed release: writes SHA256SUMS and SHA256SUMS.sig for the files it publishes (docs/release.md, "Signed releases").

.DESCRIPTION
    Lists the SHA-256 of every file vpk pack made for the release (assets.win.json in the release folder), one
    "hash  name" line each, LF line ends, and signs those exact bytes with the private key in the UPDATE_SIGNING_KEY
    environment variable. The key is taken out of the environment at once and is never written or printed.
    The signature is then checked with the public key the app carries (src/Granary.Core/Updates/update-key.pub). The
    script fails when the two keys are not a pair, so no release goes out that installed copies would refuse.

.EXAMPLE
    ./scripts/sign-release.ps1 -Version 0.2.0
    The release workflow runs it in its publish job, which alone has UPDATE_SIGNING_KEY (the "release" environment).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    # As the app takes a version: [0-9] (\d takes any script's digits) and \z (a $ allows a line end after it).
    [ValidatePattern('\A[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?\z')]
    [string]$Version,
    [string]$ReleaseDir = 'artifacts/releases',
    [string]$OutputDir = 'artifacts/signed',
    [string]$PublicKeyPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'lib/update-signing.ps1')

# Out of the environment before anything else runs, so nothing this script starts inherits it.
$pem = $env:UPDATE_SIGNING_KEY
Remove-Item Env:UPDATE_SIGNING_KEY -ErrorAction SilentlyContinue
if (-not $pem) {
    throw 'UPDATE_SIGNING_KEY is not set. The release workflow sets it in its publish job, from the "release" environment.'
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$packId = 'GranaryApp'
if (-not $PublicKeyPath) {
    $PublicKeyPath = $script:UpdateKeyPath
}

Push-Location $repoRoot
try {
    $publicBlob = Read-UpdatePublicKey -Path (Resolve-Path $PublicKeyPath).Path
    $releaseDir = (Resolve-Path $ReleaseDir).Path

    # The files of this build, as vpk pack lists them; a previous release downloaded for deltas is not one of them.
    $assets = Get-Content -Raw (Join-Path $releaseDir 'assets.win.json') | ConvertFrom-Json
    [string[]]$names = @(foreach ($asset in $assets) { $asset.RelativeFileName })
    [Array]::Sort($names, [StringComparer]::Ordinal)
    $full = "$packId-$Version-full.nupkg"
    if ($names -cnotcontains $full) {
        throw "$full is not among the files vpk pack made in $ReleaseDir; pack $Version first."
    }
    $lines = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt $names.Length; $i++) {
        $name = $names[$i]
        # The app takes only plain names, each once.
        if ($name -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or ($i -gt 0 -and $names[$i - 1] -ceq $name)) {
            throw "assets.win.json lists '$name', which is not a plain file name listed once."
        }
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $releaseDir $name)).Hash.ToLowerInvariant()
        [void]$lines.Append("$hash  $name`n")
    }
    $manifest = [Text.UTF8Encoding]::new($false).GetBytes($lines.ToString())

    $key = Open-UpdatePrivateKey -Pem $pem
    $pem = $null
    try {
        # The workflow's key and the app's must be a pair: otherwise every installed copy would refuse this release.
        $ownPublic = $key.Export([System.Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
        if ([Convert]::ToBase64String($ownPublic) -cne [Convert]::ToBase64String($publicBlob)) {
            throw "UPDATE_SIGNING_KEY is not the private key of $PublicKeyPath. Nothing was signed (docs/release.md, 'Signed releases')."
        }
        $signature = New-UpdateSignature -Key $key -Data $manifest
    }
    finally {
        $key.Dispose()
    }
    if (-not (Test-UpdateSignature -PublicBlob $publicBlob -Data $manifest -Signature $signature)) {
        throw "The signature does not verify with $PublicKeyPath. Nothing was written."
    }

    New-Item -ItemType Directory -Force $OutputDir | Out-Null
    $outputDir = (Resolve-Path $OutputDir).Path
    [IO.File]::WriteAllBytes((Join-Path $outputDir 'SHA256SUMS'), $manifest)
    [IO.File]::WriteAllText((Join-Path $outputDir 'SHA256SUMS.sig'), [Convert]::ToBase64String($signature) + "`n", [Text.Encoding]::ASCII)
    Write-Host "Signed $($names.Length) files of Granary $Version into $OutputDir`:"
    Write-Host $lines.ToString().TrimEnd()
}
finally {
    Pop-Location
}
