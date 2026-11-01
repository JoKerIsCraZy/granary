<#
.SYNOPSIS
    Shared by check-libmpv.ps1 and update-libmpv.ps1: the libmpv build native.lock.json pins, the official mpv CI builds
    it comes from, and the FFmpeg version a build carries (docs/release.md, "libmpv").

.DESCRIPTION
    Everything that comes from GitHub's API or from a download is untrusted. A name, version, hash or date is used only
    once it matches one of the strict patterns here, and only the matched text goes into a path, a URL, a command line,
    a file of the repository or a pull request. A downloaded libmpv-2.dll is only read as bytes, never loaded or run.
    Runs in Windows PowerShell 5.1 and in PowerShell 7.
#>

$script:RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:LockPath = Join-Path $script:RepoRoot 'native.lock.json'

# Upstream: the release mpv's CI replaces on every master build. Granary bundles its x86_64 LGPL libmpv asset, whose name
# carries the mpv version and commit and the CI run that built it. Patterns use [0-9] (\d takes any script's digits) and
# \z (a $ allows a line end after it).
$script:UpstreamRepository = 'mpv-player/mpv'
$script:UpstreamTag = 'git-release'
$script:FFmpegRepository = 'FFmpeg/FFmpeg'
$script:AssetPattern = '\Alibmpv-(?<version>v[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(?:-[0-9a-z]{1,16}){0,3}-g(?<commit>[0-9a-f]{7,40}))-(?<run>[0-9]{1,19})-x86_64-w64-mingw32-lgpl\.zip\z'
$script:RepositoryPattern = '\A[A-Za-z0-9-]{1,39}/(?!\.{1,2}\z)[A-Za-z0-9._-]{1,100}\z'
$script:Sha256Pattern = '\A[0-9a-f]{64}\z'

# Bounds for what is downloaded and read: today the zip is about 56 MB, its DLL about 127 MB, FFmpeg's sources 18 MB.
# The DLL is read whole into one string, so its bound also keeps that string well inside .NET's limit.
$script:MinDownloadBytes = 1MB
$script:MaxZipBytes = 512MB
$script:MaxDllBytes = 512MB
$script:MaxSourceBytes = 512MB

$script:Utf8NoBom = New-Object System.Text.UTF8Encoding $false

# The parts of an upstream asset name, or $null when it is not the x86_64 LGPL libmpv build.
function ConvertFrom-LibmpvAssetName {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Name)
    $match = [regex]::Match($Name, $script:AssetPattern)
    if (-not $match.Success) {
        return $null
    }
    return [pscustomobject]@{
        Name    = $Name
        Version = $match.Groups['version'].Value
        Commit  = $match.Groups['commit'].Value
        Run     = [decimal]$match.Groups['run'].Value
    }
}

# FFmpeg's FFMPEG_VERSION as its ffbuild/version.sh makes it: git-<date>-<hash> from a shallow clone (mpv's CI builds
# FFmpeg that way) or N-<count>-g<hash> from a full one. $null for any other form, such as a release number.
function ConvertFrom-FFmpegVersion {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Version)
    $shallow = [regex]::Match($Version, '\Agit-(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})-(?<commit>[0-9a-f]{7,40})\z')
    if ($shallow.Success) {
        return [pscustomobject]@{ Version = $Version; Commit = $shallow.Groups['commit'].Value; Date = $shallow.Groups['date'].Value }
    }
    $described = [regex]::Match($Version, '\A(?:N|n[0-9]{1,3}(?:\.[0-9]{1,3}){1,2})-[0-9]{1,7}-g(?<commit>[0-9a-f]{7,40})\z')
    if ($described.Success) {
        return [pscustomobject]@{ Version = $Version; Commit = $described.Groups['commit'].Value; Date = $null }
    }
    return $null
}

function Assert-Repository {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Repository)
    if ($Repository -cnotmatch $script:RepositoryPattern) {
        throw "Not a GitHub repository (owner/name): $Repository"
    }
}

function Get-LockString {
    param([Parameter(Mandatory)]$Entry, [Parameter(Mandatory)][string]$Key)
    $property = $Entry.PSObject.Properties[$Key]
    if (-not $property -or $property.Value -isnot [string]) {
        throw "native.lock.json has no libmpv.$Key."
    }
    return $property.Value
}

<#
The pin, checked: the upstream asset it came from, its mirror on Granary's repository, its SHA-256 and the FFmpeg version
its DLL carries. Every field must agree with the others, so nothing else needs to trust the file's text. -Text checks a
new version of the file before it is written.
#>
function Read-LibmpvLock {
    param([string]$Text = '')
    if (-not $Text) {
        $Text = [IO.File]::ReadAllText($script:LockPath, $script:Utf8NoBom)
    }
    $root = $Text | ConvertFrom-Json
    $entry = $null
    if ($root -and $root.PSObject.Properties['libmpv']) {
        $entry = $root.libmpv
    }
    if (-not $entry) {
        throw 'native.lock.json has no libmpv entry.'
    }
    $upstreamUrl = Get-LockString $entry 'upstream'
    $prefix = "https://github.com/$script:UpstreamRepository/releases/download/$script:UpstreamTag/"
    $asset = $null
    if ($upstreamUrl.StartsWith($prefix, [StringComparison]::Ordinal)) {
        $asset = ConvertFrom-LibmpvAssetName $upstreamUrl.Substring($prefix.Length)
    }
    if (-not $asset) {
        throw "native.lock.json's libmpv.upstream is not an x86_64 LGPL libmpv asset of $script:UpstreamRepository's ${script:UpstreamTag}: $upstreamUrl"
    }
    $version = Get-LockString $entry 'version'
    if ($version -cne $asset.Version) {
        throw "native.lock.json's libmpv.version ($version) is not the version of its upstream asset ($($asset.Version))."
    }
    $url = Get-LockString $entry 'url'
    $mirror = [regex]::Match($url, '\Ahttps://github\.com/(?<repository>[^/]+/[^/]+)/releases/download/(?<tag>[^/]+)/')
    $tags = @((Get-MirrorTag $asset), (Get-MirrorTag $asset -Legacy))
    if (-not $mirror.Success -or $mirror.Groups['repository'].Value -cnotmatch $script:RepositoryPattern -or
        $tags -cnotcontains $mirror.Groups['tag'].Value -or
        $url -cne "https://github.com/$($mirror.Groups['repository'].Value)/releases/download/$($mirror.Groups['tag'].Value)/$($asset.Name)") {
        throw "native.lock.json's libmpv.url is not the mirror of its upstream asset: $url"
    }
    $sha256 = Get-LockString $entry 'sha256'
    if ($sha256 -cnotmatch $script:Sha256Pattern) {
        throw "native.lock.json's libmpv.sha256 is not a SHA-256: $sha256"
    }
    $ffmpeg = ConvertFrom-FFmpegVersion (Get-LockString $entry 'ffmpeg')
    if (-not $ffmpeg) {
        throw "native.lock.json's libmpv.ffmpeg is not an FFmpeg version with a commit: $($entry.ffmpeg)"
    }
    return [pscustomobject]@{
        Version    = $version
        Url        = $url
        Sha256     = $sha256
        Asset      = $asset
        FFmpeg     = $ffmpeg
        Repository = $mirror.Groups['repository'].Value
        MirrorTag  = $mirror.Groups['tag'].Value
    }
}

<#
The mirror release's tag: deps-libmpv-<version>-<CI run>, so a rebuild of one mpv commit gets a release of its own. The
first mirror was tagged before the run was part of it (-Legacy); native.lock.json may still point at it.
#>
function Get-MirrorTag {
    param([Parameter(Mandatory)]$Asset, [switch]$Legacy)
    if ($Legacy) {
        return "deps-libmpv-$($Asset.Version)"
    }
    return "deps-libmpv-$($Asset.Version)-$($Asset.Run)"
}

function Get-MirrorAssetUrl {
    param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)]$Asset)
    return "https://github.com/$Repository/releases/download/$(Get-MirrorTag $Asset)/$($Asset.Name)"
}

<#
The x86_64 LGPL libmpv assets of upstream's git-release, with the size, SHA-256 and upload date GitHub lists, newest run
first. Other assets (other platforms, the GPL variant, the player) are skipped. jq's @tsv escapes tabs and line breaks
inside a field, so no name can add a line of its own.
#>
function Get-UpstreamLibmpvAssets {
    $lines = @(& gh api "repos/$script:UpstreamRepository/releases/tags/$script:UpstreamTag" --jq '.assets[] | [.name, (.size | tostring), (.digest | tostring), .updated_at] | @tsv')
    if ($LASTEXITCODE -ne 0) {
        throw "Could not read the $script:UpstreamTag release of $script:UpstreamRepository (gh api exit code $LASTEXITCODE)."
    }
    $assets = foreach ($line in $lines) {
        $fields = ([string]$line).Split([char]9)
        if ($fields.Count -ne 4) {
            continue
        }
        $asset = ConvertFrom-LibmpvAssetName $fields[0]
        if (-not $asset) {
            continue
        }
        if ($fields[1] -cnotmatch '\A[0-9]{1,12}\z') {
            throw "GitHub lists $($asset.Name) without a valid size."
        }
        $sha256 = $null
        if ($fields[2] -cmatch '\Asha256:(?<hash>[0-9a-f]{64})\z') {
            $sha256 = $Matches['hash']
        }
        $date = $null
        if ($fields[3] -cmatch '\A(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\z') {
            $date = $Matches['date']
        }
        $asset | Add-Member -NotePropertyMembers @{ Size = [long]$fields[1]; Sha256 = $sha256; Uploaded = $date } -PassThru
    }
    return @($assets | Sort-Object -Property Run -Descending)
}

# The full SHA and committer date (yyyy-MM-dd) of a commit, or $null with a warning when GitHub does not answer.
function Get-CommitInfo {
    param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Commit)
    if ($Commit -cnotmatch '\A[0-9a-f]{7,40}\z') {
        throw "Not a commit: $Commit"
    }
    $line = @(& gh api "repos/$Repository/commits/$Commit" --jq '[.sha, .commit.committer.date] | @tsv') -join "`n"
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not look up commit $Commit of $Repository (gh api exit code $LASTEXITCODE)."
        return $null
    }
    $match = [regex]::Match($line, '\A(?<sha>[0-9a-f]{40})\t(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\z')
    if (-not $match.Success -or -not $match.Groups['sha'].Value.StartsWith($Commit, [StringComparison]::Ordinal)) {
        Write-Warning "GitHub's answer for commit $Commit of $Repository is not a commit."
        return $null
    }
    return [pscustomobject]@{ Sha = $match.Groups['sha'].Value; Date = $match.Groups['date'].Value }
}

# Copies a stream and stops with an error as soon as more than MaxBytes have come, whatever the source claimed.
function Copy-StreamCapped {
    param(
        [Parameter(Mandatory)][IO.Stream]$Source,
        [Parameter(Mandatory)][IO.Stream]$Target,
        [Parameter(Mandatory)][long]$MaxBytes,
        [Parameter(Mandatory)][string]$What
    )
    $buffer = New-Object byte[] 1048576
    $total = 0L
    while (($read = $Source.Read($buffer, 0, $buffer.Length)) -gt 0) {
        $total += $read
        if ($total -gt $MaxBytes) {
            throw "$What is larger than the $MaxBytes bytes it may have."
        }
        $Target.Write($buffer, 0, $read)
    }
}

<#
Downloads a URL to a new file and stops after MaxBytes, so a wrong or hostile answer cannot fill the disk. A download
that fails leaves no file behind.
#>
function Save-Download {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][long]$MaxBytes,
        [Parameter(Mandatory)][string]$What
    )
    Write-Host "Downloading $Url"
    Add-Type -AssemblyName System.Net.Http
    $client = New-Object System.Net.Http.HttpClient
    $created = $false
    try {
        $client.Timeout = [TimeSpan]::FromMinutes(10)
        $client.DefaultRequestHeaders.UserAgent.ParseAdd('granary-libmpv-update')
        $response = $client.GetAsync($Url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            if (-not $response.IsSuccessStatusCode) {
                throw "Downloading $What failed with HTTP $([int]$response.StatusCode)."
            }
            $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            try {
                $target = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                $created = $true
                try {
                    Copy-StreamCapped -Source $source -Target $target -MaxBytes $MaxBytes -What $What
                }
                finally {
                    $target.Dispose()
                }
            }
            finally {
                $source.Dispose()
            }
        }
        finally {
            $response.Dispose()
        }
    }
    catch {
        if ($created -and (Test-Path -LiteralPath $Path)) {
            Remove-Item -LiteralPath $Path -Force
        }
        throw
    }
    finally {
        $client.Dispose()
    }
}

<#
Downloads an upstream asset into a directory and checks its size and SHA-256 against GitHub's listing. The URL is built
from the checked name, never taken from the API. Returns the file's path and SHA-256; a file that fails a check is
deleted. Without a SHA-256 in the listing nothing is downloaded, since the pin would then rest on whatever came.
#>
function Save-UpstreamAsset {
    param([Parameter(Mandatory)]$Asset, [Parameter(Mandatory)][string]$Directory)
    if ($Asset.Size -lt $script:MinDownloadBytes -or $Asset.Size -gt $script:MaxZipBytes) {
        throw "GitHub lists $($Asset.Name) with $($Asset.Size) bytes, outside the expected 1 MB to 512 MB."
    }
    if (-not $Asset.Sha256) {
        throw "GitHub lists no SHA-256 for $($Asset.Name), so a download could not be checked. Run again later."
    }
    $path = Join-Path $Directory $Asset.Name
    $url = "https://github.com/$script:UpstreamRepository/releases/download/$script:UpstreamTag/$($Asset.Name)"
    Save-Download -Url $url -Path $path -MaxBytes $Asset.Size -What $Asset.Name
    $length = (Get-Item -LiteralPath $path).Length
    if ($length -ne $Asset.Size) {
        Remove-Item -LiteralPath $path -Force
        throw "$($Asset.Name) came as $length bytes, not the $($Asset.Size) GitHub lists. Upstream may have replaced it meanwhile; run again."
    }
    $sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
    if ($sha256 -cne $Asset.Sha256) {
        Remove-Item -LiteralPath $path -Force
        throw "$($Asset.Name) has the SHA-256 $sha256, not the $($Asset.Sha256) GitHub lists. It was deleted."
    }
    return [pscustomobject]@{ Path = $path; Sha256 = $sha256 }
}

# The names of the entries of a zip, without unpacking anything.
function Get-ZipEntryNames {
    param([Parameter(Mandatory)][string]$ZipPath)
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        return @($zip.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $zip.Dispose()
    }
}

<#
Copies libmpv-2.dll out of a libmpv zip to a new file, and nothing else: the destination is fixed, never built from the
entry's name, and the copy stops at MaxDllBytes whatever size the zip claims.
#>
function Export-LibmpvDll {
    param([Parameter(Mandatory)][string]$ZipPath, [Parameter(Mandatory)][string]$Destination)
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entries = @($zip.Entries | Where-Object { $_.FullName -ceq 'libmpv-2.dll' })
        if ($entries.Count -ne 1) {
            throw "$([IO.Path]::GetFileName($ZipPath)) has $($entries.Count) entries named libmpv-2.dll, not one."
        }
        if ($entries[0].Length -gt $script:MaxDllBytes) {
            throw "libmpv-2.dll in $([IO.Path]::GetFileName($ZipPath)) claims $($entries[0].Length) bytes, more than 512 MB."
        }
        $source = $entries[0].Open()
        try {
            $target = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                Copy-StreamCapped -Source $source -Target $target -MaxBytes $script:MaxDllBytes -What "libmpv-2.dll in $([IO.Path]::GetFileName($ZipPath))"
            }
            finally {
                $target.Dispose()
            }
        }
        finally {
            $source.Dispose()
        }
    }
    finally {
        $zip.Dispose()
    }
}

<#
The FFmpeg version a libmpv-2.dll carries. FFmpeg is linked into the DLL, and each of its libraries keeps
"FFmpeg version <FFMPEG_VERSION>" as a string of its own (av_codec_ffversion and its siblings), so a build names its FFmpeg
several times: at least one copy must be there, and all must agree. The file is read as bytes only.
#>
function Get-FFmpegVersionFromDll {
    param([Parameter(Mandatory)][string]$Path)
    $text = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($Path))
    $versions = New-Object 'System.Collections.Generic.SortedSet[string]' -ArgumentList ([StringComparer]::Ordinal)
    foreach ($match in [regex]::Matches($text, '(?<![\x20-\x7e])FFmpeg version (?<version>[0-9A-Za-z][0-9A-Za-z.+~_-]{0,63})\x00')) {
        [void]$versions.Add($match.Groups['version'].Value)
    }
    $name = [IO.Path]::GetFileName($Path)
    if ($versions.Count -eq 0) {
        throw "$name names no FFmpeg version."
    }
    if ($versions.Count -gt 1) {
        throw "$name names several FFmpeg versions: $(@($versions) -join ', ')."
    }
    return @($versions)[0]
}

# The newest x86_64 LGPL libmpv build upstream offers.
function Get-NewestUpstreamAsset {
    $assets = @(Get-UpstreamLibmpvAssets)
    if ($assets.Count -eq 0) {
        throw "The $script:UpstreamTag release of $script:UpstreamRepository has no x86_64 LGPL libmpv asset any more. Look at what it offers and adapt scripts/lib/libmpv.ps1."
    }
    return $assets[0]
}

<#
Whether an upstream build replaces the pinned one: a later CI run of another mpv commit. A rebuild of the pinned commit
brings nothing new from mpv and waits for the next commit; the update branch (deps/libmpv-<version>) is named by the
commit too.
#>
function Test-NewerBuild {
    param([Parameter(Mandatory)]$Lock, [Parameter(Mandatory)]$Asset)
    return ($Asset.Version -cne $Lock.Version) -and ($Asset.Run -gt $Lock.Asset.Run)
}

function Format-Commit {
    param([Parameter(Mandatory)][string]$Commit, $Info, [string]$FallbackDate = '')
    if ($Info) {
        return "``$Commit``, committed $($Info.Date)"
    }
    if ($FallbackDate) {
        return "``$Commit``, committed $FallbackDate"
    }
    return "``$Commit``, commit date unknown"
}

<#
Step outputs of a workflow (GITHUB_OUTPUT), and nothing outside one. A value is one line of checked text, so it cannot
add an output of its own.
#>
function Write-StepOutput {
    param([Parameter(Mandatory)][System.Collections.Specialized.OrderedDictionary]$Values)
    if (-not $env:GITHUB_OUTPUT) {
        return
    }
    $lines = foreach ($key in $Values.Keys) {
        $value = [string]$Values[$key]
        if ($key -cnotmatch '\A[a-z][a-z0-9-]{0,40}\z' -or $value -cnotmatch '\A[0-9A-Za-z .,:/\\()+_-]{0,300}\z') {
            throw "Not a step output this script writes: $key"
        }
        "$key=$value"
    }
    [IO.File]::AppendAllText($env:GITHUB_OUTPUT, (($lines -join "`n") + "`n"), $script:Utf8NoBom)
}

# Adds Markdown to the job's summary in a workflow (GITHUB_STEP_SUMMARY), and nothing outside one.
function Add-StepSummary {
    param([Parameter(Mandatory)][string]$Markdown)
    if ($env:GITHUB_STEP_SUMMARY) {
        [IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, $Markdown.TrimEnd() + "`n", $script:Utf8NoBom)
    }
}

# The FFmpeg version in a libmpv zip: libmpv-2.dll goes to a fresh folder under $WorkDir just to be read, then is removed.
function Get-FFmpegVersionFromZip {
    param([Parameter(Mandatory)][string]$ZipPath, [Parameter(Mandatory)][string]$WorkDir)
    $folder = Join-Path $WorkDir ('dll-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $folder | Out-Null
    try {
        $dll = Join-Path $folder 'libmpv-2.dll'
        Export-LibmpvDll -ZipPath $ZipPath -Destination $dll
        return Get-FFmpegVersionFromDll -Path $dll
    }
    finally {
        Remove-Item -LiteralPath $folder -Recurse -Force
    }
}
