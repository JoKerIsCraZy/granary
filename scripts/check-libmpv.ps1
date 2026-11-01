<#
.SYNOPSIS
    Compares the libmpv build native.lock.json pins with the newest official mpv CI build (docs/release.md, "libmpv").

.DESCRIPTION
    Reads the x86_64 LGPL libmpv assets of mpv-player/mpv's git-release release through `gh api` and prints the pinned
    build and the newest one, with the link to mpv's changes between them. It downloads and changes nothing: the FFmpeg
    version of a newer build shows with ./scripts/update-libmpv.ps1, which reads it from the DLL.

    A later CI run of another mpv commit counts as newer; a rebuild of the pinned commit does not. In a workflow
    (GITHUB_OUTPUT set) it also writes the step outputs newer (true or false), asset and version, which
    .github/workflows/native-deps.yml passes on. It needs the GitHub CLI, signed in or with GH_TOKEN set.

.EXAMPLE
    ./scripts/check-libmpv.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'lib/libmpv.ps1')

$lock = Read-LibmpvLock
$newest = Get-NewestUpstreamAsset
$isNewer = Test-NewerBuild -Lock $lock -Asset $newest
$uploaded = 'unknown'
if ($newest.Uploaded) {
    $uploaded = $newest.Uploaded
}

Write-Host "Pinned: $($lock.Asset.Name), FFmpeg $($lock.FFmpeg.Version)"
Write-Host "Newest: $($newest.Name), uploaded $uploaded"
if ($isNewer) {
    $compare = "https://github.com/$script:UpstreamRepository/compare/$($lock.Asset.Commit)...$($newest.Commit)"
    Write-Host "A newer build is available. mpv's changes: $compare"
    Write-Host 'Its FFmpeg version: ./scripts/update-libmpv.ps1 (a dry run, which changes nothing).'
    Add-StepSummary "A newer libmpv build is available: ``$($newest.Name)``, uploaded $uploaded. Pinned: ``$($lock.Asset.Name)``.`n`nmpv's changes: $compare"
}
else {
    if ($newest.Name -cne $lock.Asset.Name) {
        Write-Host 'Upstream rebuilt the pinned mpv commit, or offers an older run; that is not an update.'
    }
    Write-Host 'native.lock.json pins the newest build.'
    Add-StepSummary "native.lock.json pins the newest libmpv build, ``$($lock.Asset.Name)``."
}

$outputs = [ordered]@{
    newer   = $isNewer.ToString().ToLowerInvariant()
    asset   = $newest.Name
    version = $newest.Version
}
Write-StepOutput $outputs
