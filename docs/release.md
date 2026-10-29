# Releases and updates

Granary installs and updates itself with [Velopack](https://velopack.io), the way Plezy (another media client)
does with Inno Setup and WinSparkle, in one tool: a per-user setup without administrator rights, a portable zip, and
update packages that an installed copy downloads in the background and applies once it closes.

## What a release contains

`scripts/pack-release.ps1 -Version 0.2.0` publishes the app self-contained for win-x64 and packs it into
`artifacts/releases`:

| File | For |
|---|---|
| `GranaryApp-win-Setup.exe` | Installing: per user to `%LOCALAPPDATA%\GranaryApp`, with Start menu and desktop shortcuts named "Granary", no administrator rights. Uninstall from Windows' Apps settings. |
| `GranaryApp-win-Portable.zip` | Running without installing; it also updates itself. |
| `GranaryApp-<version>-full.nupkg`, `-delta.nupkg` | The update packages; a delta holds only what changed since the previous release. |
| `releases.win.json`, `RELEASES`, `assets.win.json` | The release feed installed copies read. |

The package id `GranaryApp` names the install folder and must never change: an installed copy only updates from
releases with its id. It is not `Granary` because `%LOCALAPPDATA%\Granary` is the data folder (servers, sign-ins,
cache, logs, `AppPaths.DataDirectory`), and uninstalling removes the install folder with everything in it.

The app is unpackaged (`WindowsPackageType` None) but sets `EnableMsixTooling`: without it `dotnet publish` leaves
out the app's resources (`Granary.pri`, the compiled XAML), and the published copy fails at its first window with
"XAML parsing failed".

## How an installed copy updates

- `Program.Main` runs `VelopackApp.Build().Run()` before WinUI starts: Velopack's setup, updater and uninstaller run
  Granary's exe with their own arguments, and it does its part and exits before a window opens.
- `VelopackUpdater` (App) is the app's `IAppUpdater`. It looks for releases on the GitHub repository the build names
  (`-p:GranaryUpdateRepository=https://github.com/owner/repo`, kept in the assembly's metadata). A build without
  one, a development build, or a copy not installed by the setup never looks (the log says why).
- `AppUpdatesViewModel` (Core) looks once Granary has shown its first page, then every six hours while it runs, and at
  once on Settings → About's Check for updates (one look at a time, whoever asks). A newer release is downloaded in
  the background, its progress shown in About; then a notice at the bottom right offers "Restart now" (the window
  closes as usual, playback stops first, Velopack applies the update and starts Granary again) or "Later" (it is
  applied silently once Granary closes). A failed look is logged, shown in About, and tried again at the next one.
- What a release changes comes from CHANGELOG.md ([Keep a Changelog](https://keepachangelog.com)): the app ships the
  file and shows it in Settings → About, and `pack-release.ps1` hands the version's section to `vpk pack
  --releaseNotes`, so a downloaded update says what's new in it before the restart.

## Making a release

1. Raise `Version` in `Directory.Build.props` (SemVer, for example `0.2.0`), and in CHANGELOG.md turn `## [Unreleased]`
   into `## [0.2.0] - 2026-11-01` (a new empty Unreleased above it). Without that section the release goes out without
   notes (the script warns).
2. Commit, then tag and push: `git tag v0.2.0` and `git push origin v0.2.0`.
3. `.github/workflows/release.yml` fetches libmpv, runs the tests, downloads the previous release (for the delta),
   packs with the repository's URL and publishes the release with `GITHUB_TOKEN`. It can also be run by hand with a
   version.

Locally, `./scripts/pack-release.ps1 -Version 0.2.0` builds the same files without publishing; pass `-Repository` for
a build that looks for updates.

## Before the first public release

- The repository must be public: installed copies read its releases without credentials.
- Security reports: turn on **Private vulnerability reporting** under the repository's Settings → Code security, since
  `SECURITY.md` sends every report there and names no other channel.
- libmpv: `native.lock.json` already points at this repository's mirror release
  `deps-libmpv-v0.41.0-dev-ga39482e7a`, which holds the exact upstream zip (same SHA-256; upstream's `git-release`
  asset is gone) and the mpv and FFmpeg sources the LGPL asks to offer. Upload it once, right after the first push
  (a release needs a commit to tag), then run the CI job again; until then its libmpv step fails. From the
  repository's root, with the pinned zip in `native\dl-libmpv` where `fetch-native.ps1 -ZipPath` used it:

  ```powershell
  $dl = "native\dl-libmpv"
  Invoke-WebRequest https://github.com/mpv-player/mpv/archive/a39482e7a.tar.gz -OutFile "$dl\mpv-a39482e7a.tar.gz"
  Invoke-WebRequest https://github.com/FFmpeg/FFmpeg/archive/1d83e71.tar.gz -OutFile "$dl\ffmpeg-1d83e71.tar.gz"
  # A prerelease: the app's updater (GithubSource, prerelease: false) and the "latest release" badge skip it.
  gh release create deps-libmpv-v0.41.0-dev-ga39482e7a --repo JoKeRIsCraZy/granary --prerelease `
      --title "libmpv v0.41.0-dev-ga39482e7a (mirror)" `
      --notes "The mpv CI build Granary bundles, unchanged, with the mpv and FFmpeg sources it was built from. Not an app release." `
      "$dl\libmpv-v0.41.0-dev-ga39482e7a-40099161509-x86_64-w64-mingw32-lgpl.zip" "$dl\mpv-a39482e7a.tar.gz" "$dl\ffmpeg-1d83e71.tar.gz"
  Remove-Item native\libmpv\.sha256; ./scripts/fetch-native.ps1   # downloads from the mirror and checks the SHA-256
  ```

  A new libmpv later is a new mirror release with its own tag, and `url`, `sha256`, `version` and
  `THIRD_PARTY_NOTICES.md` change together.
- Signing: nothing is signed yet, so Windows SmartScreen warns about an unknown publisher on the first download.
  `vpk pack` signs with `--signParams` (a code-signing certificate) or `--azureTrustedSignFile` (Azure Trusted
  Signing).
- winget: once releases are public, a manifest can be submitted, and kept current per release, as Plezy does with
  `winget-releaser`.
