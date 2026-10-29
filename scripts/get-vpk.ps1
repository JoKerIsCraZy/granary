<#
.SYNOPSIS
    Fetches vpk, Velopack's packing tool, at the version dotnet-tools.json names, checks it against the SHA-256 pinned
    here, and prints the path of its vpk.dll (docs/release.md, "Packing tools").

.DESCRIPTION
    Releases are packed and uploaded with this copy, never with one NuGet resolves. The package comes from nuget.org,
    must have the pinned SHA-256, and is unpacked afresh into artifacts/tools each time. A new vpk version (Dependabot
    raises dotnet-tools.json) stops here until its SHA-256 is added below, once checked against nuget.org's own record of
    the package (the catalog's SHA-512 packageHash).

.EXAMPLE
    $vpk = ./scripts/get-vpk.ps1
    dotnet $vpk pack --help
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot

# SHA-256 of vpk.<version>.nupkg as nuget.org serves it. 1.2.161: its SHA-512 matches the catalog's packageHash
# (NZ9I8Fbw…2/ixmw==, published 2026-09-29).
$pinned = @{
    '1.2.161' = 'ac9be73846d9a7a76483f8684abd1503cd50703b992727429dcdc045589c5e08'
}

$version = (Get-Content -Raw (Join-Path $repoRoot 'dotnet-tools.json') | ConvertFrom-Json).tools.vpk.version
if (-not $pinned.ContainsKey($version)) {
    throw "vpk $version (dotnet-tools.json) has no pinned SHA-256 in scripts/get-vpk.ps1. Check the package against nuget.org's catalog and add it (docs/release.md, 'Packing tools')."
}
$expected = $pinned[$version]

$cache = Join-Path $repoRoot 'artifacts/tools'
New-Item -ItemType Directory -Force $cache | Out-Null
$nupkg = Join-Path $cache "vpk.$version.nupkg"
if (-not (Test-Path -LiteralPath $nupkg) -or (Get-FileHash -Algorithm SHA256 -LiteralPath $nupkg).Hash -ne $expected) {
    Write-Host "Downloading vpk $version from nuget.org"
    Invoke-WebRequest -UseBasicParsing -Uri "https://api.nuget.org/v3-flatcontainer/vpk/$version/vpk.$version.nupkg" -OutFile $nupkg
}
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $nupkg).Hash.ToLowerInvariant()
if ($actual -cne $expected) {
    Remove-Item -LiteralPath $nupkg -Force
    throw "vpk.$version.nupkg has the SHA-256 $actual, not the pinned $expected. It was deleted, and nothing ran it."
}

# Unpacked afresh from the checked package, so nothing left in the folder from before runs.
$tool = Join-Path $cache "vpk-$version"
if (Test-Path -LiteralPath $tool) {
    Remove-Item -LiteralPath $tool -Recurse -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($nupkg, $tool)
$dll = Join-Path $tool 'tools/net10.0/any/vpk.dll'
if (-not (Test-Path -LiteralPath $dll)) {
    throw "vpk.$version.nupkg has no tools/net10.0/any/vpk.dll."
}
return $dll
