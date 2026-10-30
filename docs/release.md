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
| `GranaryApp-<version>-full.nupkg` | The update package. There are no delta packages (`vpk pack --delta None`): only the full package is signed byte for byte. |
| `releases.win.json`, `RELEASES` | The release feed installed copies read; `vpk upload` writes them as it uploads. `assets.win.json` lists the files `vpk pack` made and stays local. |
| `SHA256SUMS`, `SHA256SUMS.sig` | Added by the release workflow: the SHA-256 of every file `vpk pack` made, and the signature over that list (see "Signed releases"). |

The package id `GranaryApp` names the install folder and must never change: an installed copy only updates from
releases with its id. It is not `Granary` because `%LOCALAPPDATA%\Granary` is the data folder (servers, sign-ins,
cache, logs, `AppPaths.DataDirectory`), and uninstalling removes the install folder with everything in it.

The app is unpackaged (`WindowsPackageType` None) but sets `EnableMsixTooling`: without it `dotnet publish` leaves
out the app's resources (`Granary.pri`, the compiled XAML), and the published copy fails at its first window with
"XAML parsing failed".

## How an installed copy updates

- `Program.Main` runs `VelopackApp.Build().SetAutoApplyOnStartup(UpdateSafety.ApplyPackagesAtStart).Run()` (false)
  before WinUI starts: Velopack's setup, updater and uninstaller run Granary's exe with their own arguments, and it
  does its part and exits before a window opens. Velopack's own apply-on-start is off, so a package in its packages
  folder is never applied unless it was verified.
- `VelopackUpdater` (App) is the app's `IAppUpdater`. It looks for releases on the GitHub repository the build names
  (`-p:GranaryUpdateRepository=https://github.com/owner/repo`, kept in the assembly's metadata). A build without
  one or without the update signing key, a development build, or a copy not installed by the setup never looks (the
  log says why). It downloads and applies only a release that is verified as signed (see "Signed releases").
- `AppUpdatesViewModel` (Core) looks once Granary has shown its first page, then every six hours while it runs, and at
  once on Settings → About's Check for updates (one look at a time, whoever asks). A newer release is downloaded in
  the background, its progress shown in About; then a notice at the bottom right offers "Restart now" (the window
  closes as usual, playback stops first, Velopack applies the update and starts Granary again) or "Later" (it is
  applied silently once Granary closes). A failed look is logged, shown in About, and tried again at the next one, and
  so is an update that could not be verified.
- Settings → About → Automatic updates (on by default) turns that schedule off. The choice is the app's, not a
  server's or a profile's, and is kept in `updates.json` in the data directory (`JsonUpdatePreferences`, so
  `GRANARY_DATA_DIR` moves it too). Without the file updates are automatic; a file that is there but cannot be read
  (cut short by a crash or a full disk) turns them off until they are switched on, since it may have said off. The
  file is written off the UI thread, and closing waits up to two seconds for a save still running.
- Off, Granary does not look at the start or every six hours. Check for updates only checks: a newer release shows as
  available with its version only (the feed's notes are not signed, and the verified package is not downloaded yet)
  and a link to its GitHub release. Nothing is downloaded until the user chooses Download and install, which
  downloads and verifies it exactly as an automatic update; then it is ready, with the verified notes, and applies on
  Restart to update or when Granary closes, whatever the switch does afterwards. A download that fails on the way
  leaves the release available, so Download and install tries again.
- An update already downloaded when the switch is turned off, and not chosen with Download and install, is held
  back: it is not applied on close, and shows as available until Download and install (no second download: it was
  verified, and is hashed once more as Granary closes) or until the switch is turned on again, which readies it with
  the notice. Turned on again, the schedule resumes: the next look is six hours after the last check, or at once if
  that has passed or a release found meanwhile is waiting.
- The version shown and linked comes from the feed, which is not signed, so Granary takes only a plain one
  (`FeedVersion`: "0.3.0", or "0.3.0-beta.1" with a label of at most 32 characters). Any other counts as a failed
  check: nothing is shown, linked or downloaded, and the log does not repeat it.
- What a release changes comes from CHANGELOG.md ([Keep a Changelog](https://keepachangelog.com)): the app ships the
  file and shows it in Settings → About, and `pack-release.ps1` hands the version's section to `vpk pack
  --releaseNotes`, so a downloaded update says what's new in it before the restart. Those notes are read from the
  verified package's nuspec, which the signature covers, never from the unsigned feed.

## Signed releases

An installed copy applies an update only when its release is signed with Granary's update key, the idea Silage's
updater uses. The GitHub release itself is not trusted: a leaked `GITHUB_TOKEN` or a swapped asset must not reach
anyone's PC.

**What a release carries.** `SHA256SUMS` lists the SHA-256 of every file `vpk pack` made (the setup, the portable zip
and the full package, as `assets.win.json` names them), one `hash  name` line each with LF line ends, as `sha256sum`
writes it. `SHA256SUMS.sig` is the base64 of the signature over the exact bytes of `SHA256SUMS`. `releases.win.json`
and `RELEASES` are not in the list, since `vpk upload` writes them while it uploads. The app trusts nothing in them
that the signed list does not confirm.

**The algorithm.** ECDSA on P-256 over SHA-256, the signature in IEEE P1363 form (r and s, 32 bytes each). .NET 10 has
no Ed25519 of its own, and P-256 is in `System.Security.Cryptography` and in Windows' CNG, which the scripts use, so
they run in Windows PowerShell 5.1 and in PowerShell 7 alike (`scripts/lib/update-signing.ps1`).

**The keys.** The private key exists only in the `UPDATE_SIGNING_KEY` secret of the repository's `release`
environment. The public key is in `src/Granary.Core/Updates/update-key.pub`, which Granary.Core embeds. Until the
one-time setup below, that file is a placeholder, and then:

- A build trusts no update, and `VelopackUpdater` does not look for one.
- `pack-release.ps1 -Repository` refuses to pack, so the release workflow stops before it publishes anything.

**How a release is signed.** The release workflow has two jobs:

- `build` can only read the repository and gets no secret. It runs only on main, runs the tests, packs, and hands
  `artifacts/releases` on as an artifact.
- `publish` runs in the `release` environment, so it waits for a reviewer's approval, and only then gets
  `UPDATE_SIGNING_KEY` and permission to write. It stops if a tag or a release for the version exists already. It runs
  `scripts/sign-release.ps1`, which takes the key out of the environment, never writes or prints it, and stops unless
  the key is the pair of `update-key.pub`. Then `vpk upload` makes a draft release for the commit the run built, the
  job adds `SHA256SUMS` and `SHA256SUMS.sig` to it, and publishes it, which creates the tag. A client never sees the
  release without them.

**How the app checks an update.** `SignedReleaseVerifier` (Core), through the `IReleaseFiles` port, goes in this order:

1. The feed's version must be a plain one (`0.2.0`, `0.3.0-beta.1`), or nothing is read. It is the only part of the
   feed that goes into an address; the package id is the installed copy's own.
2. It fetches `SHA256SUMS` and `SHA256SUMS.sig` from `<repository>/releases/download/v<version>/`, at most 64 KiB and
   1 KiB.
3. The signature must verify over the exact bytes with the key the app carries. Only then is the list read.
4. The list must name `GranaryApp-<version>-full.nupkg` once, under a plain name. That binds the version: another
   release's signed list does not name this version's package.
5. The feed must offer that file under that hash. Only then may Velopack download it. Velopack checks its download
   against the feed's hash, and takes Update.exe out of the package as soon as the download is done, so this check
   must come before the download.
6. A file already under the package's name in Velopack's packages folder must hash to the signed value too, or it is
   removed first: Velopack takes such a file for the download.
7. After the download, the file must hash to the signed value. The notes shown in About are then read from that file.
8. As Granary closes, the file is hashed once more before Velopack's updater is told to apply it.

Delta packages are off on both sides (`--delta None` when packing, `UpdateSafety.MaximumDeltas`, -1, in the app): a
package rebuilt from deltas is zipped anew and never matches the signed hash. A test checks that the app hands both
`UpdateSafety` values to Velopack.

**When the check fails.** It can fail in several ways:

- The release has no `SHA256SUMS` or no `.sig`.
- The signature is bad, or made with another key.
- The list does not name the package.
- The feed or the download differs from the list.
- GitHub cannot be reached.

Whatever the cause, nothing is applied. Settings → About says "The update to Granary 0.3.0 couldn't be verified, so it
wasn't installed. Granary will try again later." The log says "Granary 0.3.0 is not installed:" and the reason. For a
network failure it gives only the kind of failure (an HTTP status, an exception type), never an address, since a
redirect can carry a token. A downloaded package that is not the signed one is deleted; if it cannot be, the log says
so, and it is still never applied. The next look, six hours later or on Check for updates, tries again; with automatic
updates off, only Check for updates does, and About leaves out "Granary will try again later."

**What this does not cover.** The check fails closed: whatever goes wrong, nothing unsigned is applied. Someone who
can write to the releases can still freeze updates, though never install one:

- An unsigned release with a higher version, or a feed entry that names one, makes every look fail until it is
  removed or a newer signed release replaces it.
- Deleting or hiding releases withholds updates. Nothing in a signature expires, so a copy cannot tell that it is
  being kept on an old version.

On the user's own PC:

- A program running as the same Windows user could swap the package in the short window between its last hash, as
  Granary closes, and Update.exe reading it. While Granary hashes the package and reads its notes it holds the file
  open, so nobody can change it then. Such a program could change Granary's installed files directly anyway.
- `new-update-key.ps1` sees PowerShell's logging policies, but not a `Start-Transcript` started by hand or by a profile.
  Run it in a fresh `powershell -NoProfile` window.

**One-time key setup** (the maintainer, once, before the first release):

1. Create the `release` environment (see "Before the first public release").
2. Sign in to the GitHub CLI with admin rights on the repository: `gh auth login`.
3. Use a machine without PowerShell logging policies (module logging, script block logging, transcription), since
   module logging can keep what a pipeline carries. The script stops when such a policy is on. It cannot see a
   transcript started by hand or by a profile, so open a fresh `powershell -NoProfile` window for it.
4. In that window, from the repository's root:
   `powershell -NoProfile -File ./scripts/new-update-key.ps1 -Repository owner/granary`. It checks that the
   environment exists and makes the key pair in memory. It stores the private key with
   `gh secret set UPDATE_SIGNING_KEY --env release`, reading it from standard input, never from a file or the command
   line. Only then does it write the public key to `update-key.pub`.
5. Commit `src/Granary.Core/Updates/update-key.pub` and push. Releases from then on are signed, and copies built with
   that key accept only them.

**A lost or leaked key.** Installed copies carry exactly one public key. A new key means they refuse every later
release, so their users must install the next release's setup by hand. If the key leaks:

1. Delete the secret.
2. Run `new-update-key.ps1 -Replace`.
3. Release, and tell users to reinstall.

The secret cannot be read back from GitHub, so it cannot be lost by deleting a local copy, because there is none. It
is lost if the secret or the repository is deleted.

## Making a release

1. Raise `Version` in `Directory.Build.props` (SemVer, for example `0.2.0`), and in CHANGELOG.md turn `## [Unreleased]`
   into `## [0.2.0] - 2026-11-01` (a new empty Unreleased above it). The release workflow requires that section, since
   it is what the update says it changes.
2. Get that onto main. Do not tag it yourself: the workflow tags the commit it builds.
3. Actions → Release → Run workflow, on `main`, with the version. `.github/workflows/release.yml` fetches libmpv, runs
   the tests and packs with the repository's URL. Then it waits for approval of the `publish` job under the run's
   "Review deployments".
4. Approving releases a commit: everything in it ships signed. Before you approve, look at what changed since the
   last release, for example `git log --stat v0.1.0..<the run's commit>` or the compare view between the two.
5. Approved, the workflow checks that the version is new, signs the release, uploads it as a draft for that commit,
   adds `SHA256SUMS` and its signature, and publishes it, which creates the tag `v0.2.0`.

Locally, `./scripts/pack-release.ps1 -Version 0.2.0` builds the same files without publishing. Pass `-Repository`
for a build that looks for updates, once `update-key.pub` holds the key.

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
  - **"Allow administrators to bypass configured protection rules":** off, so the approval holds for an admin too and
    every release is confirmed on purpose.
  - **Deployment branches and tags:** "Selected branches and tags" with the branch `main` only. A run from any other
    branch would bring that branch's copy of the workflow; this rule keeps it away from the signing key.
  - Then run the one-time key setup (see "Signed releases"). The workflow's other secret is `GITHUB_TOKEN`, which
    GitHub provides.
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
  an action's update like code. A Velopack update comes on its own: check `VelopackUpdater`'s assumptions against it
  (apply-on-start, deltas, when Update.exe is taken out of the package), and pin a new vpk (see "Packing tools").
- **Checks on every change:**
  - `codeql.yml` scans the C# code and the workflows (Security → Code scanning).
  - `dependency-review.yml` fails a pull request that brings a package with a known vulnerability.
  - `workflow-checks.yml` runs actionlint and zizmor whenever a workflow changes.
  - On GitHub's free plan, code scanning and dependency review need a public repository, so those two skip themselves
    while it is private.
  - With the repository public, also turn on Secret scanning with Push protection and Dependabot alerts under
    Settings → Advanced Security.
- Security reports: turn on **Private vulnerability reporting** under the repository's Settings → Code security, since
  `SECURITY.md` sends every report there and names no other channel.
- libmpv: `native.lock.json` already points at this repository's mirror release
  `deps-libmpv-v0.41.0-dev-ga39482e7a`, which holds the exact upstream zip (same SHA-256; upstream's `git-release`
  asset is gone) and the mpv and FFmpeg sources the LGPL asks to offer. Upload it once, right after the first push
  (a release needs a commit to tag), then run the CI job again; until then its libmpv step fails. The workflows fetch
  it with their own read-only token through `gh`, so this works while the repository is still private. From the
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
- Code signing (Authenticode, apart from the update signing above): the exe and setup are not signed yet, so Windows
  SmartScreen warns about an unknown publisher on the first download. `vpk pack` signs with `--signParams` (a
  code-signing certificate) or `--azureTrustedSignFile` (Azure Trusted Signing).
- winget: once releases are public, a manifest can be submitted, and kept current per release, as Plezy does with
  `winget-releaser`.
