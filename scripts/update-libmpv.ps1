<#
.SYNOPSIS
    Moves the libmpv pin to a newer official mpv CI build (docs/release.md, "libmpv").

.DESCRIPTION
    Takes the newest x86_64 LGPL libmpv asset of mpv-player/mpv's git-release release, or the one -AssetName names, and:

    - downloads it, checks its size and SHA-256 against GitHub's listing, and checks that it holds the files
      native.lock.json takes from it;
    - reads the FFmpeg version compiled into its libmpv-2.dll, as bytes only: nothing downloaded is ever run;
    - downloads the mpv and FFmpeg sources at the commits the build names, which the LGPL asks to offer beside it;
    - works out the new native.lock.json and THIRD_PARTY_NOTICES.md, and the text of the commit and pull request.

    Without -Publish that is all: a dry run that prints what would change and writes only to the download folder.
    With -Publish it then creates the prerelease deps-libmpv-<version> on the mirror repository with the zip and both
    source archives (or checks the one an earlier run made) and writes the two files. It commits nothing:
    .github/workflows/native-deps.yml commits, pushes and opens the pull request from the step outputs branch, title,
    commit-file and body-file.

    It needs the GitHub CLI, signed in or with GH_TOKEN set; -Publish needs the right to create releases.

.EXAMPLE
    ./scripts/update-libmpv.ps1
    Shows what moving to the newest build would change, and changes nothing.

.EXAMPLE
    ./scripts/update-libmpv.ps1 -Publish
    Mirrors the newest build and moves the pin to it. The commit and the pull request are then yours to make.
#>
[CmdletBinding()]
param(
    # Creates the mirror release and writes native.lock.json and THIRD_PARTY_NOTICES.md.
    [switch]$Publish,
    # The upstream asset to take; by default the newest. The workflow passes the one its check found, so it never
    # takes a build it did not check.
    [string]$AssetName = '',
    # The mirror repository, as owner/name. By default the one native.lock.json points at.
    [string]$Repository = '',
    # Where the downloads go, each build in a folder of its own. By default native/dl-libmpv, which git ignores.
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'lib/libmpv.ps1')

$NoticesPath = Join-Path $script:RepoRoot 'THIRD_PARTY_NOTICES.md'
$script:MirrorNotes = 'The mpv CI build Granary bundles, unchanged, with the mpv and FFmpeg sources it was built from. Not an app release.'

function Select-UpstreamAsset {
    param([string]$Name)
    if (-not $Name) {
        return Get-NewestUpstreamAsset
    }
    if (-not (ConvertFrom-LibmpvAssetName $Name)) {
        throw "Not an x86_64 LGPL libmpv asset name: $Name"
    }
    $found = @(Get-UpstreamLibmpvAssets | Where-Object { $_.Name -ceq $Name })
    if ($found.Count -ne 1) {
        throw "Upstream no longer offers ${Name}: it has moved on to a newer build. Run again."
    }
    return $found[0]
}

# A fresh, empty folder for one build's downloads. Its name is the build's checked version.
function New-DownloadFolder {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)]$Asset)
    $folder = Join-Path $Root $Asset.Version
    if (Test-Path -LiteralPath $folder) {
        Remove-Item -LiteralPath $folder -Recurse -Force
    }
    [void][IO.Directory]::CreateDirectory($folder)
    return $folder
}

# native.lock.json's libmpv entry, refused when it holds a field this script would drop by rewriting the file.
function Get-LockEntry {
    param([Parameter(Mandatory)][string]$Text)
    $root = $Text | ConvertFrom-Json
    $top = @($root.PSObject.Properties | ForEach-Object { $_.Name })
    if ($top.Count -ne 1 -or $top[0] -cne 'libmpv') {
        throw 'native.lock.json holds more than libmpv; teach update-libmpv.ps1 to keep the rest.'
    }
    $known = @('version', 'variant', 'url', 'sha256', 'ffmpeg', 'files', 'upstream', 'note')
    foreach ($name in @($root.libmpv.PSObject.Properties | ForEach-Object { $_.Name })) {
        if ($known -cnotcontains $name) {
            throw "native.lock.json's libmpv has a field update-libmpv.ps1 does not know: $name"
        }
    }
    return $root.libmpv
}

# The files fetch-native.ps1 takes from the zip: plain file names only.
function Get-LockFiles {
    param([Parameter(Mandatory)]$Entry)
    $files = @($Entry.files)
    if ($files.Count -eq 0) {
        throw "native.lock.json's libmpv.files is empty."
    }
    foreach ($file in $files) {
        if ($file -isnot [string] -or $file -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z') {
            throw "native.lock.json's libmpv.files names something that is not a plain file name: $file"
        }
    }
    return [string[]]$files
}

function Assert-ZipHolds {
    param([Parameter(Mandatory)][string]$ZipPath, [Parameter(Mandatory)][string[]]$Files)
    $names = @(Get-ZipEntryNames -ZipPath $ZipPath)
    foreach ($file in $Files) {
        $count = @($names | Where-Object { $_ -ceq $file }).Count
        if ($count -ne 1) {
            throw "$([IO.Path]::GetFileName($ZipPath)) holds $count entries named $file, not one."
        }
    }
}

function Get-RequiredCommit {
    param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Commit)
    $info = Get-CommitInfo -Repository $Repository -Commit $Commit
    if (-not $info) {
        throw "GitHub does not know commit $Commit of $Repository, so its sources cannot be mirrored."
    }
    return $info
}

<#
Downloads the sources of a commit as GitHub packs them. There is no hash to check them against: the URL names the full
commit, and the file must be a gzip archive of a plausible size.
#>
function Save-SourceArchive {
    param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Sha, [Parameter(Mandatory)][string]$Path)
    if ($Sha -cnotmatch '\A[0-9a-f]{40}\z') {
        throw "Not a full commit: $Sha"
    }
    $url = "https://github.com/$Repository/archive/$Sha.tar.gz"
    Write-Host "Downloading $url"
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $Path
    $length = (Get-Item -LiteralPath $Path).Length
    $head = New-Object byte[] 2
    $stream = [IO.File]::OpenRead($Path)
    try {
        [void]$stream.Read($head, 0, 2)
    }
    finally {
        $stream.Dispose()
    }
    if ($length -lt $script:MinDownloadBytes -or $length -gt $script:MaxSourceBytes -or $head[0] -ne 0x1f -or $head[1] -ne 0x8b) {
        Remove-Item -LiteralPath $Path -Force
        throw "The sources of $Repository at $Sha did not come as a gzip archive of 1 MB to 512 MB ($length bytes)."
    }
}

function ConvertTo-JsonString {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)
    if ($Value -match '[\x00-\x1f]') {
        throw 'A value for native.lock.json holds a control character.'
    }
    return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}

<#
native.lock.json for the new build, in the file's own layout and line endings. The variant, the files and the note stay
as they are.
#>
function New-LockText {
    param(
        [Parameter(Mandatory)][string]$OriginalText,
        [Parameter(Mandatory)]$Asset,
        [Parameter(Mandatory)][string]$Sha256,
        [Parameter(Mandatory)][string]$FFmpegVersion,
        [Parameter(Mandatory)][string]$Repository
    )
    $entry = Get-LockEntry $OriginalText
    $variant = Get-LockString $entry 'variant'
    if ($variant -cne 'lgpl') {
        throw "native.lock.json pins the $variant variant; this script handles the LGPL one only."
    }
    $files = (Get-LockFiles $entry | ForEach-Object { ConvertTo-JsonString $_ }) -join ', '
    $upstream = "https://github.com/$script:UpstreamRepository/releases/download/$script:UpstreamTag/$($Asset.Name)"
    $newline = "`n"
    if ($OriginalText.Contains("`r`n")) {
        $newline = "`r`n"
    }
    $lines = @(
        '{'
        '  "libmpv": {'
        "    `"version`": $(ConvertTo-JsonString $Asset.Version),"
        "    `"variant`": $(ConvertTo-JsonString $variant),"
        "    `"url`": $(ConvertTo-JsonString (Get-MirrorAssetUrl -Repository $Repository -Asset $Asset)),"
        "    `"sha256`": $(ConvertTo-JsonString $Sha256),"
        "    `"ffmpeg`": $(ConvertTo-JsonString $FFmpegVersion),"
        "    `"files`": [ $files ],"
        "    `"upstream`": $(ConvertTo-JsonString $upstream),"
        "    `"note`": $(ConvertTo-JsonString (Get-LockString $entry 'note'))"
        '  }'
        '}'
    )
    return ($lines -join $newline) + $newline
}

<#
THIRD_PARTY_NOTICES.md for the new build: every place that names the pinned build, its commits or its source archives
names the new one. Stops when the file does not name what native.lock.json pins, rather than guess.
#>
function New-NoticesText {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)]$Lock,
        [Parameter(Mandatory)]$Asset,
        [Parameter(Mandatory)][string]$Sha256,
        [Parameter(Mandatory)]$FFmpeg
    )
    $pairs = @(
        , @($Lock.Asset.Name, $Asset.Name, 'asset')
        , @($Lock.Version, $Asset.Version, 'version')
        , @($Lock.Sha256, $Sha256, 'SHA-256')
        , @($Lock.FFmpeg.Version, $FFmpeg.Version, 'FFmpeg version')
        , @("mpv-$($Lock.Asset.Commit).tar.gz", "mpv-$($Asset.Commit).tar.gz", 'mpv source archive')
        , @("ffmpeg-$($Lock.FFmpeg.Commit).tar.gz", "ffmpeg-$($FFmpeg.Commit).tar.gz", 'FFmpeg source archive')
        , @("``$($Lock.Asset.Commit)``", "``$($Asset.Commit)``", 'mpv commit')
        , @("``$($Lock.FFmpeg.Commit)``", "``$($FFmpeg.Commit)``", 'FFmpeg commit')
    )
    foreach ($pair in $pairs) {
        if (-not $Text.Contains($pair[0])) {
            throw "THIRD_PARTY_NOTICES.md does not name the pinned $($pair[2]), $($pair[0]). Bring its libmpv section in line with native.lock.json by hand, then run again."
        }
        $Text = $Text.Replace($pair[0], $pair[1])
    }
    foreach ($pair in $pairs) {
        if ($pair[0] -cne $pair[1] -and $Text.Contains($pair[0])) {
            throw "THIRD_PARTY_NOTICES.md still names the old $($pair[2]), $($pair[0]), after the update."
        }
    }
    return $Text
}

function New-PullRequestBody {
    param($Lock, $Asset, $FFmpeg, $MpvOld, $MpvNew, $FFmpegOld, $FFmpegNew, [string]$Repository)
    $tag = Get-MirrorTag $Asset
    $ffmpegChanges = "https://github.com/$script:FFmpegRepository/compare/$($Lock.FFmpeg.Commit)...$($FFmpeg.Commit)"
    if ($FFmpegOld -and $FFmpegOld.Sha -ceq $FFmpegNew.Sha) {
        $ffmpegChanges = 'none, both builds carry the same FFmpeg'
    }
    $lines = @(
        'Moves libmpv to a newer official mpv CI build. FFmpeg is compiled into `libmpv-2.dll`, so it changes with it.'
        ''
        '| | Pinned now | This pull request |'
        '|---|---|---|'
        "| Build | ``$($Lock.Asset.Name)`` | ``$($Asset.Name)`` |"
        "| mpv | $(Format-Commit $Lock.Asset.Commit $MpvOld) | $(Format-Commit $Asset.Commit $MpvNew) |"
        "| FFmpeg | $(Format-Commit $Lock.FFmpeg.Commit $FFmpegOld $Lock.FFmpeg.Date) (``$($Lock.FFmpeg.Version)``) | $(Format-Commit $FFmpeg.Commit $FFmpegNew) (``$($FFmpeg.Version)``) |"
        ''
        "- mpv changes: https://github.com/$script:UpstreamRepository/compare/$($Lock.Asset.Commit)...$($Asset.Commit)"
        "- FFmpeg changes: $ffmpegChanges"
        '- FFmpeg security fixes: https://ffmpeg.org/security.html'
        ''
        "The build and its mpv and FFmpeg sources are mirrored unchanged as the prerelease [``$tag``](https://github.com/$Repository/releases/tag/$tag)."
        ''
        '**Before merging**'
        ''
        '- [ ] CI is green. The workflow started it on this branch; to run every pull request check, close and reopen this pull request.'
        '- [ ] A video plays: on this branch, `./scripts/fetch-native.ps1`, then `dotnet run --project src/Granary.App -- --play <file>`.'
        '- [ ] If the update matters to users, such as an FFmpeg security fix, a line in `CHANGELOG.md` under `[Unreleased]`.'
        ''
        'Opened by the native-deps workflow (docs/release.md, "libmpv"). Closed without merging, this build is left alone and the next newer one gets a pull request of its own.'
    )
    return ($lines -join "`n") + "`n"
}

function New-CommitMessage {
    param($Lock, $Asset, $FFmpeg, [string]$Title)
    $lines = @(
        $Title
        ''
        "Pins the official mpv CI build $($Asset.Name)"
        "and the FFmpeg compiled into its libmpv-2.dll, $($FFmpeg.Version) (was $($Lock.FFmpeg.Version))."
        "The build and its mpv and FFmpeg sources are mirrored unchanged as the prerelease $(Get-MirrorTag $Asset)."
    )
    return ($lines -join "`n") + "`n"
}

# The release for a tag, or $null. A draft (an interrupted upload) and a second release for the tag are reported as such.
function Get-MirrorRelease {
    param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Tag)
    $lines = @(& gh api "repos/$Repository/releases?per_page=100" --paginate --jq '.[] | [.tag_name, (.draft | tostring), (.id | tostring)] | @tsv')
    if ($LASTEXITCODE -ne 0) {
        throw "Could not list the releases of $Repository (gh api exit code $LASTEXITCODE)."
    }
    $found = @(foreach ($line in $lines) {
            $fields = ([string]$line).Split([char]9)
            if ($fields.Count -eq 3 -and $fields[0] -ceq $Tag) {
                if ($fields[2] -cnotmatch '\A[0-9]{1,20}\z') {
                    throw "GitHub lists the release $Tag without a valid id."
                }
                [pscustomobject]@{ Id = $fields[2]; Draft = ($fields[1] -ceq 'true') }
            }
        })
    if ($found.Count -gt 1) {
        throw "$Repository has $($found.Count) releases for the tag $Tag. Keep one, then run again."
    }
    if ($found.Count -eq 0) {
        return $null
    }
    return $found[0]
}

# A published mirror release must hold every file, fully uploaded, and the zip with the SHA-256 native.lock.json pins.
function Assert-MirrorAssets {
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)]$Release,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string[]]$Names,
        [Parameter(Mandatory)][string]$ZipName,
        [Parameter(Mandatory)][string]$ZipSha256
    )
    $lines = @(& gh api "repos/$Repository/releases/$($Release.Id)" --jq '.assets[] | [.name, .state, (.digest | tostring)] | @tsv')
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read the release $Tag of $Repository (gh api exit code $LASTEXITCODE)."
    }
    $assets = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
    foreach ($line in $lines) {
        $fields = ([string]$line).Split([char]9)
        if ($fields.Count -eq 3) {
            $assets[$fields[0]] = [pscustomobject]@{ State = $fields[1]; Digest = $fields[2] }
        }
    }
    $problems = @()
    foreach ($name in $Names) {
        if (-not $assets.ContainsKey($name)) {
            $problems += "$name is missing"
        }
        elseif ($assets[$name].State -cne 'uploaded') {
            $problems += "$name is not fully uploaded"
        }
    }
    if ($assets.ContainsKey($ZipName) -and $assets[$ZipName].Digest -cne "sha256:$ZipSha256") {
        $problems += "$ZipName has the digest $($assets[$ZipName].Digest), not sha256:$ZipSha256"
    }
    if ($problems.Count -gt 0) {
        throw "The release $Tag of $Repository does not hold this build: $($problems -join '; '). A published release cannot change; look at it on GitHub."
    }
}

<#
Creates the mirror prerelease with all files in one go: gh uploads them to a draft and then publishes it, since an
immutable release takes no files once published. A release an earlier run made is checked instead. A prerelease is
skipped by the app's updater and by the "latest release" badge.
#>
function Publish-Mirror {
    param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)]$Asset, [Parameter(Mandatory)][string[]]$Files, [Parameter(Mandatory)][string]$ZipSha256)
    $tag = Get-MirrorTag $Asset
    $names = [string[]]@($Files | ForEach-Object { [IO.Path]::GetFileName($_) })
    $release = Get-MirrorRelease -Repository $Repository -Tag $tag
    if (-not $release) {
        Write-Host "Creating the prerelease $tag on $Repository"
        & gh release create --repo $Repository --prerelease --latest=false --title "libmpv $($Asset.Version) (mirror)" --notes $script:MirrorNotes -- $tag @Files
        if ($LASTEXITCODE -ne 0) {
            throw "gh release create failed with exit code $LASTEXITCODE."
        }
        $release = Get-MirrorRelease -Repository $Repository -Tag $tag
        if (-not $release) {
            throw "The release $tag of $Repository does not show after its creation."
        }
    }
    else {
        Write-Host "$tag exists on $Repository from an earlier run; checking it."
    }
    if ($release.Draft) {
        throw "The release $tag of $Repository is a draft, left from an interrupted upload. Delete it under Releases, then run again."
    }
    Assert-MirrorAssets -Repository $Repository -Release $release -Tag $tag -Names $names -ZipName $Asset.Name -ZipSha256 $ZipSha256
    Write-Host "$tag holds this build."
}

$lock = Read-LibmpvLock
if (-not $Repository) {
    $Repository = $lock.Repository
}
Assert-Repository $Repository
$asset = Select-UpstreamAsset -Name $AssetName
Write-Host "Pinned: $($lock.Asset.Name), FFmpeg $($lock.FFmpeg.Version)"
Write-Host "Taking: $($asset.Name)"
if (-not (Test-NewerBuild -Lock $lock -Asset $asset)) {
    Write-Host 'native.lock.json pins this build already, or a newer one. Nothing to do.'
    Write-StepOutput ([ordered]@{ updated = 'false' })
    exit 0
}

if (-not $OutDir) {
    $OutDir = Join-Path $script:RepoRoot 'native/dl-libmpv'
}
$folder = New-DownloadFolder -Root $OutDir -Asset $asset
$originalLock = [IO.File]::ReadAllText($script:LockPath, $script:Utf8NoBom)
$originalNotices = [IO.File]::ReadAllText($NoticesPath, $script:Utf8NoBom)

$zip = Save-UpstreamAsset -Asset $asset -Directory $folder
Assert-ZipHolds -ZipPath $zip.Path -Files (Get-LockFiles (Get-LockEntry $originalLock))
$ffmpegVersion = Get-FFmpegVersionFromZip -ZipPath $zip.Path -WorkDir $folder
$ffmpeg = ConvertFrom-FFmpegVersion $ffmpegVersion
if (-not $ffmpeg) {
    throw "$($asset.Name) carries FFmpeg $ffmpegVersion, which names no commit, so its sources cannot be mirrored."
}
Write-Host "It carries FFmpeg $($ffmpeg.Version)."

$mpvNew = Get-RequiredCommit -Repository $script:UpstreamRepository -Commit $asset.Commit
$ffmpegNew = Get-RequiredCommit -Repository $script:FFmpegRepository -Commit $ffmpeg.Commit
$mpvOld = Get-CommitInfo -Repository $script:UpstreamRepository -Commit $lock.Asset.Commit
$ffmpegOld = Get-CommitInfo -Repository $script:FFmpegRepository -Commit $lock.FFmpeg.Commit
$mpvSource = Join-Path $folder "mpv-$($asset.Commit).tar.gz"
$ffmpegSource = Join-Path $folder "ffmpeg-$($ffmpeg.Commit).tar.gz"
Save-SourceArchive -Repository $script:UpstreamRepository -Sha $mpvNew.Sha -Path $mpvSource
Save-SourceArchive -Repository $script:FFmpegRepository -Sha $ffmpegNew.Sha -Path $ffmpegSource

$newLock = New-LockText -OriginalText $originalLock -Asset $asset -Sha256 $zip.Sha256 -FFmpegVersion $ffmpeg.Version -Repository $Repository
$checked = Read-LibmpvLock -Text $newLock
if ($checked.Asset.Name -cne $asset.Name -or $checked.Sha256 -cne $zip.Sha256 -or $checked.FFmpeg.Version -cne $ffmpeg.Version) {
    throw "The new native.lock.json does not pin $($asset.Name)."
}
$newNotices = New-NoticesText -Text $originalNotices -Lock $lock -Asset $asset -Sha256 $zip.Sha256 -FFmpeg $ffmpeg

$title = "chore(deps): libmpv $($asset.Version) with FFmpeg $($ffmpeg.Commit)"
$bodyFile = Join-Path $folder 'pull-request.md'
$commitFile = Join-Path $folder 'commit-message.txt'
$body = New-PullRequestBody -Lock $lock -Asset $asset -FFmpeg $ffmpeg -MpvOld $mpvOld -MpvNew $mpvNew -FFmpegOld $ffmpegOld -FFmpegNew $ffmpegNew -Repository $Repository
[IO.File]::WriteAllText($bodyFile, $body, $script:Utf8NoBom)
[IO.File]::WriteAllText($commitFile, (New-CommitMessage -Lock $lock -Asset $asset -FFmpeg $ffmpeg -Title $title), $script:Utf8NoBom)

if (-not $Publish) {
    $tag = Get-MirrorTag $asset
    $mirror = 'does not exist yet'
    if (Get-MirrorRelease -Repository $Repository -Tag $tag) {
        $mirror = 'exists already and would be checked'
    }
    Write-Host ''
    Write-Host "Dry run: nothing was published and no file of the repository changed. The downloads are in $folder."
    Write-Host "The mirror release $tag on $Repository $mirror."
    Write-Host 'THIRD_PARTY_NOTICES.md would name the new build, its commits and its source archives.'
    Write-Host '--- native.lock.json would become:'
    Write-Host $newLock
    Write-Host "--- the pull request: $title"
    Write-Host $body
    exit 0
}

Publish-Mirror -Repository $Repository -Asset $asset -Files @($zip.Path, $mpvSource, $ffmpegSource) -ZipSha256 $zip.Sha256
[IO.File]::WriteAllText($script:LockPath, $newLock, $script:Utf8NoBom)
[IO.File]::WriteAllText($NoticesPath, $newNotices, $script:Utf8NoBom)
Write-Host "native.lock.json and THIRD_PARTY_NOTICES.md now pin $($asset.Name)."
Add-StepSummary "### $title`n`n$body"
Write-StepOutput ([ordered]@{
        updated       = 'true'
        branch        = "deps/libmpv-$($asset.Version)"
        title         = $title
        'body-file'   = $bodyFile
        'commit-file' = $commitFile
    })
