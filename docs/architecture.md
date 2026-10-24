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
