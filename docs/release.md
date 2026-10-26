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
- `AppUpdatesViewModel` (Core) looks once Granary has shown its first page, then every six hours while it runs. A
  newer release is downloaded in the background; then a notice at the bottom right offers "Restart now" (the window
  closes as usual, playback stops first, Velopack applies the update and starts Granary again) or "Later" (it is
  applied silently once Granary closes). A failed look is logged and tried again at the next one.

## Making a release

1. Raise `Version` in `Directory.Build.props` (SemVer, for example `0.2.0`).
2. Commit, then tag and push: `git tag v0.2.0` and `git push origin v0.2.0`.
3. `.github/workflows/release.yml` fetches libmpv, runs the tests, downloads the previous release (for the delta),
   packs with the repository's URL and publishes the release with `GITHUB_TOKEN`. It can also be run by hand with a
   version.

Locally, `./scripts/pack-release.ps1 -Version 0.2.0` builds the same files without publishing; pass `-Repository` for
a build that looks for updates.

## Before the first public release

- The repository must be public: installed copies read its releases without credentials.
- libmpv: the pinned upstream asset in `native.lock.json` disappears with the next mpv master build. Mirror the zip
  (for example as an asset of a release in this repository) and point `url` and `sha256` at the mirror, or the
  workflow's fetch fails. `THIRD_PARTY_NOTICES.md` also asks to offer the matching sources.
- Signing: nothing is signed yet, so Windows SmartScreen warns about an unknown publisher on the first download.
  `vpk pack` signs with `--signParams` (a code-signing certificate) or `--azureTrustedSignFile` (Azure Trusted
  Signing).
- winget: once releases are public, a manifest can be submitted, and kept current per release, as Plezy does with
  `winget-releaser`.
