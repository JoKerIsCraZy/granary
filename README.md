# Granary

A native Windows client for [Silo](https://siloserver.org), the self-hosted media server.

Granary is an independent project. It is not made, endorsed or supported by Silo Media L.L.C. "Silo" is a
trademark of Silo Media L.L.C.; Granary uses the name only to say which server it works with, as Silo's
[trademark policy](https://github.com/Silo-Server/silo-server/blob/main/TRADEMARK.md) allows.

Granary is built with C# / .NET 10, WinUI 3 (Windows App SDK) and libmpv. It plays as much as possible with
Direct Play, so the server rarely has to transcode. Its look follows Silo's web app and its behaviour follows
Silo's Android and Apple apps.

**Status:** early development (version 0.1.0). There is no public release yet.

## Screenshots

<!-- TODO: add screenshots of Home, a title page and the player before the first release. -->

Screenshots will follow with the first release.

## Features

- **Servers and sign-in:** password or a code approved on another device (with a QR code), household profiles
  with their PINs, and several servers to switch between. On the same network Granary talks to the server
  over its LAN address (the server's pinned local route), and media goes through the server's proxy nodes
  where the server says so.
- **Browsing:** Home with its rows, each library as rows, a poster grid with an A–Z bar, or collections,
  title pages for movies, series, seasons and episodes, Search including people, For You, and a Calendar of
  airings.
- **Your lists:** Favorites, Watchlist and History (with Select to remove watch data), your own collections
  and the server's, and the Notifications inbox with which series notify you, Mute per series and the
  preferences.
- **Requests:** Discover rows, studios, networks and genres, titles from TMDB with their seasons, Request to add
  from Search, your requests with their progress, and the watchlist's titles not in the library yet.
- **Player:** Direct Play and the server's streams, audio and subtitle tracks (including adding subtitles and
  their appearance), intro and credits skipping and marker editing, the next episode, chapters, previews on the
  seek bar, playback info, a mini view (picture in picture) and a bar the player minimizes to. An
  administrator's pause, stop and messages reach the player.
- **Settings:** playback, language and subtitles, card badges and the sidebar's entries per profile, servers,
  and About with Check for updates. Granary takes the server's accent colour.
- **Admin** (for a server's administrators): the Dashboard and Activity with the streams playing now.

Profiles hide libraries the same way they do on the web. The full description of each screen is in
[docs/ui-design.md](docs/ui-design.md).

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11, on x64
- A Silo server

## Install

Granary will ship as a per-user setup (no administrator rights) and as a portable zip. Both update
themselves in the background with [Velopack](https://velopack.io). The packages will be on the releases page
of this repository. <!-- TODO: link the releases page once the repository is public. -->
[docs/release.md](docs/release.md) describes what a release contains and how updates work.

## Building from source

You need the .NET 10 SDK (`global.json` pins the version) and PowerShell. No Visual Studio is needed; the
`dotnet` CLI builds everything. VS Code users get tasks and launch configurations in `.vscode/`.

```powershell
./scripts/fetch-native.ps1          # downloads libmpv pinned in native.lock.json into native/libmpv and checks its SHA-256
dotnet build Granary.slnx
dotnet test --solution Granary.slnx
dotnet run --project src/Granary.App
```

The app is x64 only. To build only the app project, pass the platform:
`dotnet build src/Granary.App/Granary.App.csproj -p:Platform=x64`. Warnings fail the build
(`TreatWarningsAsErrors` in `Directory.Build.props`).

For development:

- `--play <file or URL>` plays a video without a server:
  `dotnet run --project src/Granary.App -- --play C:\Videos\sample.mkv`.
- `--play-item <content id>` plays a title of the signed-in server.
- Set `GRANARY_DATA_DIR` to an empty folder to keep servers, sign-ins, cache and logs out of your own
  `%LOCALAPPDATA%\Granary`, for example when you run a second copy.

Coverage: `dotnet test --solution Granary.slnx --coverage --coverage-settings tests/coverage.config`.

## Project layout

```
src/Granary.Core     API client, sign-in and sessions, playback logic, view models (no UI dependency)
src/Granary.Player   libmpv interop and the playback engine
src/Granary.App      the WinUI 3 app: windows, pages, controls, platform services
tests/Granary.Tests  unit tests and libmpv integration tests
spike/               the phase 1 libmpv-in-WinUI spike (outside the product build)
scripts/             native dependency, release packing, and screenshot / debug-output helpers
docs/                architecture, API contract, UI design, releases
```

## Documentation

- [docs/architecture.md](docs/architecture.md): projects, layers, threading, security and the decisions
  behind them
- [docs/api-contract.md](docs/api-contract.md): the Silo API as Granary uses it
- [docs/ui-design.md](docs/ui-design.md): the look and every screen
- [docs/release.md](docs/release.md): the installer, updates and how to make a release
- [spike/README.md](spike/README.md): how libmpv video gets into WinUI 3, with measurements
- [CHANGELOG.md](CHANGELOG.md): what changed

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) first. Coding agents also read [AGENTS.md](AGENTS.md). Everyone
taking part follows the [Code of Conduct](CODE_OF_CONDUCT.md). Report security problems privately as
[SECURITY.md](SECURITY.md) describes, not in a public issue.

## License

AGPL-3.0-or-later, like the Silo repositories; see [LICENSE](LICENSE). Parts of Granary are adapted from
Silo's web, Android and Apple clients (Copyright (C) 2026 Silo Media L.L.C. and contributors,
AGPL-3.0-or-later). Those parts, and the third-party components with their licenses, are listed in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Silo's name and logo are not covered by the AGPL; Granary
shows Silo's mark only to refer to Silo.

## Acknowledgements

- [Silo](https://github.com/Silo-Server): the server Granary talks to, and the web, Android and Apple apps
  whose look and behaviour it follows.
- [mpv](https://mpv.io) and [FFmpeg](https://ffmpeg.org), which play the media.
- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK),
  [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet), [Velopack](https://velopack.io),
  [QRCoder](https://github.com/codebude/QRCoder), [ThumbHash](https://github.com/evanw/thumbhash) and
  [Plus Jakarta Sans](https://github.com/tokotype/PlusJakartaSans).
