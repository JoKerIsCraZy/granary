<#
.SYNOPSIS
    Downloads the native libraries pinned in native.lock.json into native/.

.DESCRIPTION
    Verifies the SHA-256 of every download before extracting it. Pass -ZipPath to use an archive
    you already have (for example a mirrored copy) instead of downloading.

.EXAMPLE
    ./scripts/fetch-native.ps1
    ./scripts/fetch-native.ps1 -ZipPath D:\mirror\libmpv-...-lgpl.zip
#>
[CmdletBinding()]
param(
    [string]$ZipPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$lock = Get-Content (Join-Path $repoRoot 'native.lock.json') -Raw | ConvertFrom-Json
$entry = $lock.libmpv
$target = Join-Path $repoRoot 'native/libmpv'
$cache = Join-Path $repoRoot 'native/cache'
$stamp = Join-Path $target '.sha256'

if ((Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $entry.sha256) -and (Test-Path (Join-Path $target 'libmpv-2.dll'))) {
    Write-Host "libmpv $($entry.version) ($($entry.variant)) is already in place."
    return
}

New-Item -ItemType Directory -Force $cache, $target | Out-Null
if (-not $ZipPath) {
    $ZipPath = Join-Path $cache ([IO.Path]::GetFileName(([Uri]$entry.url).AbsolutePath))
    if (-not (Test-Path $ZipPath)) {
        Write-Host "Downloading $($entry.url)"
        # A release asset of a private repository needs a token, which a plain download cannot send: with GH_TOKEN set
        # (the workflows pass their own read-only token), gh fetches it through the API instead.
        $release = [regex]::Match(([Uri]$entry.url).AbsolutePath, '\A/([^/]+/[^/]+)/releases/download/([^/]+)/([^/]+)\z')
        try {
            if ($env:GH_TOKEN -and $release.Success -and (Get-Command gh -ErrorAction SilentlyContinue)) {
                & gh release download $release.Groups[2].Value --repo $release.Groups[1].Value --pattern $release.Groups[3].Value --dir $cache
                if ($LASTEXITCODE -ne 0) { throw "gh release download failed with exit code $LASTEXITCODE" }
            }
            else {
                Invoke-WebRequest -Uri $entry.url -OutFile $ZipPath -UseBasicParsing
            }
        }
        catch {
            throw "Download failed ($($_.Exception.Message)). The mirror release may not be uploaded yet (docs/release.md); pass a copy of the pinned archive with -ZipPath."
        }
    }
}

$actual = (Get-FileHash -Algorithm SHA256 $ZipPath).Hash.ToLowerInvariant()
if ($actual -ne $entry.sha256) {
    throw "SHA-256 mismatch for $ZipPath`n  expected $($entry.sha256)`n  actual   $actual"
}

$tar = Join-Path $env:SystemRoot 'System32/tar.exe'
Push-Location $target
try {
    & $tar -xf $ZipPath @($entry.files)
    if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}
Set-Content -Path $stamp -Value $entry.sha256 -Encoding ascii
Write-Host "libmpv $($entry.version) ($($entry.variant)) extracted to $target"
