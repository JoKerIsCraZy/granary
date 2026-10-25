# Architecture

Granary is a native Windows client for Silo. This document describes how the code is organized and
the decisions behind it. The API it talks to is described in [api-contract.md](api-contract.md).

## Projects

```
src/Granary.Core     net10.0           API client, auth, models, repositories, view models
src/Granary.Player   net10.0           libmpv interop and the playback engine
src/Granary.App      net10.0-windows   WinUI 3 shell: windows, pages, controls, platform services
tests/Granary.Tests  net10.0-windows   unit tests and libmpv integration tests
spike/               Phase 1 spike (outside the product build)
```

Dependencies point inward only: `App → Player → Core`, and `App → Core`.

- **Core** has no UI dependency. View models use CommunityToolkit.Mvvm, which is UI-agnostic, so a
  later Avalonia front end could reuse Core unchanged (the role `shared` plays in the Android client).
  Core defines the ports the other layers implement: `IPlaybackEngine`, `IUiDispatcher`,
  `INavigationService`, `ISecretStore`.
- **Player** implements `IPlaybackEngine` on libmpv and exposes the video side as
  `ICompositionVideoOutput`. It has no WinUI dependency either; the only Windows-specific code is the
  DXGI call in `Interop/DxgiSwapChain.cs`.
- **App** is the composition root (`App.xaml.cs`) and holds everything that needs WinUI: pages,
  `MpvVideoHost`, the DispatcherQueue-based dispatcher, the navigation service, and the file logger.

## Video

mpv renders with its own d3d11 backend in composition mode (`d3d11-output-mode=composition`). The
engine reads the swap chain from the `display-swapchain` property; `MpvVideoHost` sets it on a XAML
`SwapChainPanel`, reports the panel size in physical pixels (`d3d11-composition-size`) and applies the
inverse DPI scale. XAML controls draw over the video. The comparison with the render API and a child
HWND is in [spike/README.md](../spike/README.md).

Because there is no window, mpv cannot query the monitor: the app must pass HDR target settings and
the display refresh rate itself (Phase 3).

## Threading

- libmpv events arrive on the engine's event thread; `IPlaybackEngine` raises its events there.
- View models marshal to the UI thread through `IUiDispatcher`; they never block on it.
- `MpvVideoHost` re-enters the UI thread through its `DispatcherQueue` when the swap chain changes.
- Position updates are throttled to 4 per second; state changes are reported immediately.

## Networking and security

- `HttpClient` instances come from `IHttpClientFactory`. The server probe uses a client without
  credentials and without redirects.
- JSON uses System.Text.Json source generation (`SiloJsonContext`): snake_case, optional members omitted
  on write, unknown members ignored on read. Errors are RFC 9457 Problem Details (`ApiProblem`,
  `SiloApiException`); code branches on the problem code, never on `detail`.
- Secrets live in one DPAPI-protected file per Windows user (`DpapiSecretStore`) in local app data. A file
  that can no longer be decrypted is set aside as `.unreadable-<time>`; the user signs in again.
- Logs go to `%LOCALAPPDATA%\Granary\logs`. Every line passes `LogRedactor`; mpv log lines that mention an
  `Authorization` header are dropped whole. Tokens never reach a log.
- Media requests carry their headers through mpv's `http-header-fields`. mpv keeps the headers of an
  open stream, and reports a mid-stream `401` only in its log (as end of file). The engine therefore
  watches the log and reports `PlaybackEndReason.AuthenticationFailed` instead of `EndOfFile`; the caller
  refreshes the token and reloads at the position.

## Artwork

`ArtworkLoader` (Granary.Core) loads every catalog image the app shows; the controls only decode its
bytes (`Controls/Artwork.cs`). Its work runs on the thread pool, never on the UI thread. Requests go out
over HTTP/2 without credentials (artwork URLs are presigned) and are not logged. A download has 30 seconds,
body included. One download serves every request for an image; a failed URL is not asked for again for a
minute (its renewed URL is), and the bitmap then tries the URL itself.

- Memory: the encoded bytes of the most recently used images, 96 MB.
- Disk: images the server marks `immutable`, in `cache\artwork` under the data directory, 512 MB, the
  least recently used deleted first, in the background. File names are hashes of the URL without its
  signature. Temporary files of writes that never finished are swept on the first write of a run.
- Prefetch: rows and grids queue the artwork of their cards as they arrive (`CardArtworkPrefetch`), and a
  series page queues the episode stills of every season, the season to play first. Four workers fetch
  them beside the downloads for images on screen, so a long queue never delays those.

## Server playback

A title plays through one `ServerPlayback` (Granary.Core) per start of the player page; the page's
`PlayerViewModel` shows its state in the title's own time and passes seeks to it. The protocol is in
[api-contract.md](api-contract.md), sections 8 to 10.

- **Before the plan** (`PlaybackStarter`): watch detail, the file played last (else the first whole file), the
  start position (the resume point minus 7 s from 30 s on, 0 to start over, sent explicitly), the profile's
  quality and bitrate cap (`PlaybackPreferencesReader`), the server's capabilities, and the device report:
  `MpvDeviceProbe` reads the GPU's D3D11VA decoder profiles; HEVC, VP9 and AV1 count only with hardware.
- **The plan**: its stream URL must lie beneath the saved server (`PlaybackUrl`). `MediaAuthorization` gives the
  load the bearer and profile headers with a token that has at least half its lifetime ahead; mpv keeps them
  for every request of the load. The plan starts at its own `player_start_seconds`; positions shown, reported
  and sent are source time (`player + timeline_offset_seconds`).
- **While playing**: progress every 10 s, and once a pause has held for a second (`PlaybackProgress`: one
  sequence per sample, retried with the identical body). Leaving the page by any way stops the session with
  its final position and stops the engine (`PlayerViewModel.Leave`); before the session's first frame the stop
  sends 0, which the server never stores, so a quick look does not move the resume point. Closing the window
  waits up to 4 s for stops still on their way (`PlaybackStops`). An end within 30 s of the title's end
  finishes it; an earlier end is a stream that broke off.
- **Recovery**: refused headers (401 mid-stream) → fresh token, same plan reloaded where the viewer was; a plan
  that never played (or showed no picture within 90 s) → `failure_recovery` replan with every key tried (at
  most 8); a connection lost after the plan's first frame → no-op `track_change` replans for about 2.5 minutes,
  the schedule going on while reloads break again; a session the server forgot (404), or one that refuses to go
  on → a new attempt at the position, and the old session is stopped; an administrator's stop (410) → leave; a
  seek outside a remux or transcode window → `seek_reanchor`. Replans that meet a busy or unreachable server
  are resent unchanged (1, 2, 4, 8 s). A start answered after the stop has its new session stopped at once.
- **One recovery at a time**: the server refuses a second replan while one is open, so what happens meanwhile
  (the replaced stream's end, a progress answer) is dropped or comes up again once the new stream plays, and a
  seek waits for that stream. Engine states and ends carry the id of their load (`PlaybackSnapshot.LoadId`):
  one raised before a reload can arrive after it and is ignored. A failure stops the engine; after the stop the
  playback leaves the engine alone, which may already play the next title.
- After a refused request mpv runs to the end of what it had read and pauses there before it reports the end.
  The engine keeps the last real position and pause from the refusal until the next load, so the end of that
  data is never taken for the viewer's position.
- mpv keeps the media headers for every request of a load and would forward them on a redirect or to an
  absolute URL in an HLS manifest. v2 media routes do not redirect and their manifests are relative, so only a
  server or proxy that already sees the tokens could send them elsewhere. A loopback proxy that adds the headers
  per request and only towards the saved server (the Apple client's design) would close this for good and would
  also make the reload after a token refresh unnecessary.
- `--play <file or URL>` plays without the server and `--play-item <content id>` plays a title of the
  signed-in server, for development.

## Sessions and authentication

API calls pass through two handlers on the `SiloHttp` client:

```
SiloHttp ─▶ ClientIdentityHandler ─▶ AuthenticationHandler ─▶ SocketsHttpHandler (no redirects)
```

- `SiloHttp` marks each request with its `SiloAuth` level and the server base URL it was built under.
  Both handlers act only when the request URL lies beneath that base, so neither identity headers nor
  credentials reach another origin.
- `ClientIdentityHandler` adds the `X-Silo-Device-*` / `X-Silo-Client*` headers, also on the public
  sign-in calls.
- `AuthenticationHandler` looks up the saved server for that base URL and its `SessionCredentials`
  (token pair, expiry, profile id and token, last known account), stored per server through
  `ICredentialStore` in the secret store. It refreshes a minute before expiry, refreshes once on a `401`
  and replays the request, signs out on `session_expired` or a rejected refresh, passes a refresh `503`
  (with `Retry-After`) to the caller instead of the `401`, and clears the profile on
  `profile_verification_required`.
- `TokenRefresher` makes the refresh single-flight per server: a caller whose token was already replaced
  gets the new one without a network call, and a failed refresh answers everyone waiting until its
  `Retry-After` passes. The refresh runs on its own client (identity handler only); a caller can stop
  waiting, but the refresh itself completes, so new tokens are always stored.
- Every login has a local `SessionId`. Refresh results, sign-outs and profile resets caused by a response
  apply only to the login the request was sent with, so a late answer never touches a newer login to the
  same server. A replay keeps the original profile headers and swaps only the bearer token.
- Tokens, profile ids and profile tokens must be visible ASCII before they are stored, since they go back
  out as headers. Records that carry secrets print without them (`ToString`), and `LogRedactor` also masks
  record printouts and bare JWTs.
- Plain `http://` servers need the user's consent on the sign-in page before any credentials are sent.
- `SessionManager` derives the state (`NoServer`, `SignedOut`, `PasswordChangeRequired`,
  `ProfileRequired`, `Ready`) from the registry's active server and its credentials, and follows both
  through their `Changed` events. A sign-out caused by the handler therefore reaches the UI on the same
  path as one the user started.
- `ShellViewModel` navigates on state changes. Pages change the session; they do not navigate after
  sign-in themselves.

## Native dependency

`libmpv-2.dll` is the official mpv CI build (LGPL variant), pinned with its SHA-256 in
`native.lock.json` and fetched by `scripts/fetch-native.ps1` into `native/libmpv/` (not committed). The
upstream asset is replaced on every mpv master update, so a release must mirror the pinned archive.
Licensing is in [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md).

## Decisions

| Decision | Reason |
|---|---|
| Native API `/api/v2` only | `/api/v1` is a frozen alpha bridge being retired; both reference clients are v2-only. |
| mpv composition mode on a `SwapChainPanel` | XAML over video, stable mpv line, least app code, no frame drops in the spike. |
| Own P/Invoke layer for libmpv | About 25 functions are needed; existing .NET bindings are unmaintained or lack the composition and D3D11 APIs. `LibraryImport` is trim- and AOT-safe. |
| LGPL libmpv build | 30 MB smaller than the GPL build and passed the same spike tests; Granary needs no GPL-only mpv feature. |
| Header-authenticated media (`header_authenticated_media_v1`) | No tokens in URLs or logs; same mode as the Apple client. |
| DPAPI file for tokens | The Credential Locker roams with the Microsoft account and is capped per app. |
| Session state derived from registry and credentials | One source of truth; handler-initiated sign-outs and user actions take the same path to the UI. |
| xUnit v3 with built-in asserts, no mocking library | FluentAssertions 8 is commercially licensed; fakes are small hand-written classes. |
| Central package management, warnings as errors | One place for versions; analyzers (including AOT/trim and platform checks) are enforced. |

## Building and testing

```powershell
./scripts/fetch-native.ps1
dotnet build Granary.slnx
dotnet test --solution Granary.slnx
dotnet run --project src/Granary.App -- --play native/testmedia/sdr.mkv
```

Tests run on Microsoft Testing Platform (`global.json`). libmpv integration tests skip themselves when
`libmpv-2.dll` is missing. Coverage: `dotnet test --solution Granary.slnx --coverage
--coverage-settings tests/coverage.config` (generated code excluded).

Running the app for a test or a second instance: set `GRANARY_DATA_DIR` to an empty folder, so servers,
secrets, device id and logs stay out of the user's own `%LOCALAPPDATA%\Granary`.

Two scripts help with checks the unit tests cannot make:

- `scripts/capture-window.ps1` saves a screenshot of the app window, even when it is covered.
- `scripts/capture-debug-output.ps1` records `OutputDebugString`. A `XamlParseException` at runtime
  carries no detail; WinUI names the failing resource or element there.

XAML that compiles can still fail when a page is created (for example a `StaticResource` that only
exists inside WinUI's own templates), so every new page is opened once in the running app.
