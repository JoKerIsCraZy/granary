<#
.SYNOPSIS
    Builds Granary's setup, portable zip and update packages with Velopack (docs/release.md).

.DESCRIPTION
    Fetches libmpv, publishes the app self-contained for win-x64 and packs it into artifacts/releases:
    GranaryApp-win-Setup.exe, GranaryApp-win-Portable.zip, the .nupkg update packages and releases.win.json.
    Pass -Repository for builds that look for updates there; without it the build never looks. To make delta
    updates against the previous release, download it into the output folder first (the release workflow does).

.EXAMPLE
    ./scripts/pack-release.ps1 -Version 0.2.0
    ./scripts/pack-release.ps1 -Version 0.2.0 -Repository https://github.com/owner/granary
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [string]$Repository = '',
    [string]$OutputDir = 'artifacts/releases',
    # A release must say what it changes: fail when CHANGELOG.md has no section for the version (the release workflow).
    [switch]$RequireNotes
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# The package id names the install folder, %LOCALAPPDATA%\GranaryApp: not "Granary", which is the data folder
# (servers, sign-ins, logs) that uninstalling would otherwise take with it. It must never change.
$packId = 'GranaryApp'

Push-Location $repoRoot
try {
    & (Join-Path $PSScriptRoot 'fetch-native.ps1')

    $publish = Join-Path $repoRoot 'artifacts/publish'
    if (Test-Path $publish) {
        Remove-Item $publish -Recurse -Force
    }
    dotnet publish src/Granary.App/Granary.App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained `
        "-p:Version=$Version" "-p:GranaryUpdateRepository=$Repository" -o $publish
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool restore failed with exit code $LASTEXITCODE"
    }

    # The version's section of CHANGELOG.md, "## [0.2.0] - ..." up to the next release, is what its update says it changes
    # (Settings → About, "What's new in 0.2.0").
    $notesArgs = @()
    $section = [regex]::Match((Get-Content -Raw -Encoding UTF8 (Join-Path $repoRoot 'CHANGELOG.md')),
        "(?ms)^## \[$([regex]::Escape($Version))\][^\r\n]*\r?\n(.*?)(?=^## |\z)")
    if ($section.Success -and $section.Groups[1].Value.Trim()) {
        $notes = Join-Path ([System.IO.Path]::GetTempPath()) "granary-$Version-notes.md"
        [System.IO.File]::WriteAllText($notes, $section.Groups[1].Value.Trim(), [System.Text.UTF8Encoding]::new($false))
        $notesArgs = @('--releaseNotes', $notes)
    }
    elseif ($RequireNotes) {
        throw "CHANGELOG.md has no section for $Version. Turn [Unreleased] into [$Version] first (docs/release.md)."
    }
    else {
        Write-Warning "CHANGELOG.md has no section for $Version; the release goes out without notes."
    }

    dotnet vpk pack --packId $packId --packVersion $Version --packDir $publish --mainExe Granary.exe `
        --packTitle Granary --packAuthors 'Granary contributors' --icon src/Granary.App/Assets/Logo/granary.ico --runtime win-x64 `
        --outputDir $OutputDir @notesArgs
    if ($LASTEXITCODE -ne 0) {
        throw "vpk pack failed with exit code $LASTEXITCODE"
    }
    Write-Host "Granary $Version is packed in $OutputDir."
}
finally {
    Pop-Location
}
