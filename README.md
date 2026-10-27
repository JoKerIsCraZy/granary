# Granary

A native Windows client for [Silo](https://github.com/Silo-Server), the self-hosted media server.
Granary is a working title and an independent project, not an official Silo app.

Built with C# / .NET 10, WinUI 3 (Windows App SDK) and libmpv. It aims to play as much as possible
via Direct Play so the server rarely has to transcode.

**Status:** early development. Sign-in, browsing and playback from the server work; audio and subtitle
menus, the next episode and the other player features follow next.

## Requirements

- Windows 10 (19041) or Windows 11, x64
- .NET 10 SDK
- PowerShell (for the native dependency script)

No Visual Studio is needed; the app builds with the `dotnet` CLI. VS Code users get tasks and launch
configurations in `.vscode/`.

## Build and run

```powershell
./scripts/fetch-native.ps1          # downloads and verifies libmpv into native/libmpv
dotnet build Granary.slnx
dotnet test --solution Granary.slnx
dotnet run --project src/Granary.App
```

During development `--play <file or URL>` opens a video directly:
`dotnet run --project src/Granary.App -- --play C:\Videos\sample.mkv`, and `--play-item <content id>` plays a
title of the signed-in server.

## Documentation

- [docs/architecture.md](docs/architecture.md): projects, layers, decisions
- [docs/api-contract.md](docs/api-contract.md): the Silo API as Granary uses it
- [docs/release.md](docs/release.md): the installer, updates and how to make a release
- [spike/README.md](spike/README.md): how libmpv video gets into WinUI 3, with measurements

## License

AGPL-3.0-or-later, like the other Silo repositories; see [LICENSE](LICENSE). Parts of Granary are
adapted from Silo's web, Android and Apple clients (Copyright (C) 2026 Silo Media L.L.C. and
contributors, AGPL-3.0-or-later). Those parts and third-party components with their licenses are listed
in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). "Silo" is a trademark of Silo Media L.L.C.
