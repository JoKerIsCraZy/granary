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
2. Get that onto main. Do not tag it yourself: the workflow tags the commit it builds.
3. Actions → Release → Run workflow, on `main`, with the version. `.github/workflows/release.yml` has two jobs:
   - `build` can only read the repository and gets no secret. It runs only on main, fetches libmpv, runs the tests,
     downloads the previous release (for the delta), packs with the repository's URL and hands the files on as an
     artifact.
   - `publish` runs in the `release` environment. It waits for approval under the run's "Review deployments", and
     only then may write. It checks that no tag or release for the version exists yet, then publishes the release for
     the commit the run built, which creates the tag `v0.2.0`.
4. Approving releases a commit: everything in it ships. Before you approve, look at what changed since the last
   release, for example `git log --stat v0.1.0..<the run's commit>` or the compare view between the two.

Locally, `./scripts/pack-release.ps1 -Version 0.2.0` builds the same files without publishing; pass `-Repository` for
a build that looks for updates.

## Packing tools

Every tool that touches a release is pinned by hash:

- **vpk**, Velopack's packing tool, comes through `scripts/get-vpk.ps1`, in packing and in uploading. It downloads the
  version `dotnet-tools.json` names from nuget.org and checks its SHA-256 against the value pinned in the script. It
  unpacks the package afresh into `artifacts/tools` and runs `vpk.dll` from there, never a copy NuGet resolves.
- **libmpv** is pinned by SHA-256 in `native.lock.json` (`fetch-native.ps1`).
- **Actions** are pinned by commit, with their version beside each.

When Dependabot raises vpk in `dotnet-tools.json`, `get-vpk.ps1` stops with "has no pinned SHA-256". To pin it:

1. Download `https://api.nuget.org/v3-flatcontainer/vpk/<version>/vpk.<version>.nupkg`.
2. Compare its SHA-512 (base64) with the `packageHash` of the version's entry in nuget.org's catalog
   (`https://api.nuget.org/v3/registration5-semver1/vpk/<version>.json`, then its `catalogEntry`).
3. If they agree, add its SHA-256 to the table in the script.

Not pinned by hash: the .NET SDK that `setup-dotnet` installs (by the version in `global.json`), and the build's NuGet
packages, which have no lock files yet.

## Before the first public release

- The repository must be public: installed copies read its releases without credentials.
- **The `release` environment** (Settings → Environments → New environment, named `release`):
  - **Required reviewers:** yourself. Leave "Prevent self-review" off while there is one maintainer, or nobody can
    approve. Add a second reviewer once there is a second maintainer.
  - **Deployment branches and tags:** "Selected branches and tags" with the branch `main` only. A run from any other
    branch would bring that branch's copy of the workflow; this rule keeps it away from the environment's secrets.
- **Protect main:** a branch ruleset for `main` that requires a pull request and the CI check. Whatever reaches main
  can be released.
- **Protect release tags:** Settings → Rules → Rulesets → New tag ruleset, targeting `v*`. Restrict updates and
  deletions, with only repository admins allowed to bypass, so nobody moves or removes a release's tag. Leave
  creations open: the workflow creates the tag with its own token as it publishes, and GitHub does not let that token
  bypass a ruleset. A `v*` tag someone else creates starts nothing, and the workflow refuses a version whose tag
  exists already.
- **Two-factor authentication** on every account with write access: an account takeover would otherwise approve its
  own release. In an organization, require it under Settings → Authentication security.
- **Actions defaults:** Settings → Actions → General → Workflow permissions: "Read repository contents and packages
  permissions". Leave "Allow GitHub Actions to create and approve pull requests" off. The workflows ask for more only
  where they need it.
- **Release immutability:** Settings → General → Releases: once a release is published, nobody can swap its files. The
  workflow uploads everything while the release is still a draft, so this does not get in its way.
- **Dependabot** (`.github/dependabot.yml`) proposes new versions of the pinned actions and the NuGet packages. Review
  an action's update like code, and pin a new vpk (see "Packing tools").
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
