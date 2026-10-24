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
        try {
            Invoke-WebRequest -Uri $entry.url -OutFile $ZipPath -UseBasicParsing
        }
        catch {
            throw "Download failed ($($_.Exception.Message)). The pinned asset may have been replaced upstream; download the archive from a mirror and pass -ZipPath, or re-pin url and sha256 in native.lock.json."
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
