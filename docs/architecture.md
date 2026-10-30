# Architecture

Granary is a native Windows client for Silo. This document describes how the code is organized and
the decisions behind it. The API it talks to is described in [api-contract.md](api-contract.md).

## Projects

```
src/Granary.Core     net10.0                       API client, auth, models, repositories, view models
src/Granary.Player   net10.0                       libmpv interop and the playback engine
src/Granary.App      net10.0-windows10.0.22621.0   WinUI 3 shell: windows, pages, controls, platform services
tests/Granary.Tests  net10.0-windows               unit tests and libmpv integration tests
spike/               Phase 1 spike (outside the product build)
```

Dependencies point inward only: `App → Player → Core`, and `App → Core`.

- **Core** has no UI dependency. View models use CommunityToolkit.Mvvm, which is UI-agnostic, so a
  later Avalonia front end could reuse Core unchanged (the role `shared` plays in the Android client).
  Core defines the ports the other layers implement: `IPlaybackEngine`, `IUiDispatcher`,
  `INavigationService`, `ISecretStore`, and `ISystemMediaControls` for Windows' media keys and their overlay
  (`PlayerSystemMedia` decides what they show and maps their buttons to the player's commands; the App's
  `WindowsMediaControls` drives the main window's System Media Transport Controls). `IReleaseFiles` gives
  `SignedReleaseVerifier` a release's SHA256SUMS and its signature, and the package on disk to hash and read; Core's own
  `HttpReleaseFiles` implements it, and the tests fake it (docs/release.md, "Signed releases").
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
- Media requests of server playback go through the loopback media proxy (see Server playback), which adds
  the headers per request; mpv itself gets none. mpv reports a refused request only in its log (as end of
  file), so the engine watches the log and reports `PlaybackEndReason.AuthenticationFailed` instead of
  `EndOfFile`; the playback then reloads with a fresh token, which the proxy makes rare.
- **Local route** (docs/api-contract.md, 9.7): `LocalRouteMonitor` reads `system/connections` through the
  saved address (`SiloRequestOptions.SavedAddressOnly`), reduces `local_route.url` to private IPv4
  addresses and keeps the first that presents the pinned key over HTTPS on the same port and answers
  `system/identity` with the same server id (`LocalRouteProbe`, 3 s). It checks at start, when the session
  or the network changes, and every 15 minutes; only a saved HTTPS address without a path prefix
  qualifies, and at most 4 candidates within 15 s are tried. `LocalRouteHandler` then sends that
  address's requests over the route, as Android does, all but sign-in, token refresh and sign-out
  (`/api/v2/auth/`), after the authentication handler decided the credentials by the saved address: the API calls of
  `SiloHttp` and the media proxy's requests (never a proxy node's). The server keys what it knows of a device's
  connection by the path a request came over (a start on the LAN plans as a local one; speed readings and learned
  limits are the connection's), so writes come the way the media goes. A route that does not connect, or presents
  another key, is dropped and the same request goes to the saved address, which is safe for a write too: no
  connection was made, so it cannot have reached the server; a failure after connecting is not repeated; a dropped or replaced route closes its connections after 5
  minutes, so a stream on its way is not cut. An unreachable saved address keeps a route verified before.
- Proxy-node URLs are checked as they will be sent: written without dot segments or anything encoded
  that would become one, taken in canonical form, and never on this machine or a link-local address.

## Artwork

`ArtworkLoader` (Granary.Core) loads every catalog image the app shows; the controls only decode its
bytes (`Controls/Artwork.cs`). Its work runs on the thread pool, never on the UI thread. Requests go out
over HTTP/2 without credentials (artwork URLs are presigned) and are not logged. A download has 30 seconds,
body included. One download serves every request for an image; a failed URL is not asked for again for a
minute (its renewed URL is), and the bitmap then tries the URL itself.

- Memory: the encoded bytes of the most recently used images, 96 MB, in 16 KB chunks (`ArtworkBytes`). As one array
  each, backdrops and large posters went on the large object heap, whose allocations start full collections; ordinary
  long-lived arrays the collector copies from generation to generation. So the chunks come from `ArtworkChunkPool`,
  allocated once on the pinned object heap, and go back to it (up to a sixth of the cache's size) when an image leaves
  the cache and nobody reads it any more: the bytes are counted out to their holders, the cache and each `ArtworkLease` that `GetAsync` lends. Measured
  on a 5,212-title grid scrolled 80 pages: large object heap allocations 68 MB to 4 MB, gen1 collections 6 to 1, GC
  pauses 225 to 190 ms.
- Decoding: the control copies an image's chunks into one native buffer (`Windows.Storage.Streams.Buffer`) and writes
  it once into an `InMemoryRandomAccessStream`, off the UI thread, before ending the lease. A .NET stream given to the
  decoder (`AsRandomAccessStream`) made a managed buffer as large as each of its reads, and every WinRT object .NET
  holds adds 1,000 bytes of memory pressure (CsWinRT's `IObjectReference`), enough of which starts blocking full
  collections: the remaining gen2 collections while scrolling are those.
- Disk: images the server marks `immutable`, in `cache\artwork` under the data directory, 512 MB, the
  least recently used deleted first, in the background. File names are hashes of the URL without its
  signature. Temporary files of writes that never finished are swept on the first write of a run.
- Prefetch: rows and grids queue the artwork of their cards as they arrive (`CardArtworkPrefetch`), and a
  series page queues the episode stills of every season, the season to play first. Four workers fetch
  them beside the downloads for images on screen, so a long queue never delays those.

### Cards

Rows and grids (`MediaRow`, `MediaGrid`) are `ItemsRepeater`s that recycle their cards (`MediaCardView`): a card
scrolled out is given the next title, so what a card does per title is what scrolling costs. Every gen2 collection
also walks all live XAML that .NET holds (the reference tracker), so its pause grows with the cards alive.

- A card shows a title in this order: the thumbhash's blur at once, its badges, its texts, then its artwork. While a
  scroller moves fast the artwork waits (`ScrollMotion`); a card just made also waits for its first measure, so the
  artwork is decoded at the height it shows (`DecodePixelHeight`), never a guess; a card whose width then changes by
  more than a tenth (another poster size, a breakpoint, a row's card given its width) decodes it again.
- A recycled card reuses what it has: badge views that draw the same badge only change their word
  (`CardBadgeViews.Reuse`), brushes are shared per colour, styles and brushes are looked up once, and its size,
  badge placement, subtitle style and caption colour are set only when they differ from the card before. Each new
  WinRT object would add memory pressure toward a blocking full collection (see Decoding above).
- Thumbhashes are decoded on the thread pool when a list arrives (`Thumbhashes.Warm`, from `CardArtworkPrefetch`);
  the UI thread only makes the 32 px bitmap, and keeps the latest 256.
- Repeaters make cards half a screen past the view on each side (`VerticalCacheLength`/`HorizontalCacheLength` 1, Home's
  rows too), not ItemsRepeater's default whole screen: on an ultrawide window that halves several hundred live cards.
- Switching pages sets the sidebar's pane mode only when it changes: NavigationView lays itself out again for every
  mode it is given.

Measured at 1920 px with compact posters, two runs each: share of time the UI thread stalled over 50 ms in fast
sidebar switches 32 % to 9 %, paging down a 5,215-title grid 78 % to 5 % (19 to 125 frames a second), paging back up
58 % to 3 %; GC pauses over the runs 4.1 s to 1.3 s, memory 760 to 565 MB. Maximized on a 3840 px ultrawide it is
not smooth yet: fast switches stall 81 % to 61 %, paging the grid 95 % to 89 %, GC pauses 50 s to 19 s over a run,
memory 1.6 to 1.05 GB, and single gen2 pauses that XAML starts (DisconnectUnusedReferenceSources) still take up to
5.5 s: the live cards of the cached pages are what each collection walks.

## Server playback

A title plays through one `ServerPlayback` (Granary.Core) per start of the player page; the page's
`PlayerViewModel` shows its state in the title's own time and passes seeks to it. The protocol is in
[api-contract.md](api-contract.md), sections 8 to 10.

- **Before the plan** (`PlaybackStarter`): watch detail, the file played last (else the first whole file), the
  start position (the resume point minus 7 s from 30 s on, 0 to start over, sent explicitly), the profile's
  quality and bitrate cap (`PlaybackPreferencesReader`), the server's capabilities, and the device report:
  `MpvDeviceProbe` reads the GPU's D3D11VA decoder profiles; HEVC, VP9 and AV1 count only with hardware.
- **The plan**: its stream URL must lie beneath the saved server, or, where the server offers
  `authorized_media_origins_v1` and the start confirms it, be the session's own stream on a proxy node
  (`PlaybackUrl.ResolveStream`, Apple's rules). A node URL the client refuses, or a node stream that never
  shows a picture, goes back as `sourceRefused` (once per chain), and the server replays the plan at home. `MediaAuthorization` checks
  the login and profile before the load and hands out the bearer and profile headers. mpv plays through the
  media proxy (below) and gets no credential. The plan starts at its own `player_start_seconds`; positions shown, reported
  and sent are source time (`player + timeline_offset_seconds`). A direct play (`original_http`) is the file
  with all its audio tracks, so the load selects the plan's audio track (mpv `aid`, the ordinal plus 1);
  otherwise mpv would play the one the file marks as default. Streams the server makes carry only that track.
  Each load starts with no subtitle (`sid=no`): mpv would otherwise show a track the file marks as default, a
  second authority the Android client switches off as well. The plan's track follows once the stream plays.
- **Audio tracks** (Apple's and Android's behaviour, 8.8): the menu lists the playing file's tracks with the
  one that plays checked. In a direct play the pick switches in mpv (`aid`), checked by reading back the track
  that plays (a switch that did not take puts the track before back); otherwise, or when mpv refuses, a
  `track_change` replan asks for a stream with that track, from where the viewer is. A refused track change
  leaves the stream that plays alone. The pick then goes with every later replan and reload, a new attempt starts with it, and
  every load names its audio stream (`aid=auto` without one), so a switch never carries over to the next file.
  A pick made while the stream is being replaced or replanned applies once the new one plays, unless an edition
  fallback changed the file meanwhile. Only the viewer's picks are
  saved (`PUT /api/v2/audio-prefs/{series or title}` with the ordinal, the language when it is a tag the server
  takes, and the catalog's signature of the track), in order, and closing the app waits for them with the stop.
- **Subtitles** (8.8, 8.8.1, 9.6): the start sends the track `SubtitleAutoSelection` resolves from the watch
  detail's effective fields, Android's AutoSubtitleResolver ported (a saved series signature first, then
  language, forced and SDH rules; embedded before external before downloaded; burn-ins last), as a combined
  ordinal; leaving it out means none. Once a load's stream plays, its track shows: an embedded one of a direct
  play from the file itself (mpv's track with that FFmpeg index), anything else from its sidecar, opened as a
  media-proxy route of its own and read with `sub-add` off the UI thread, at a delay of
  `-timeline_offset_seconds` (cues are source time). A burn-in shows nothing in mpv. A pick switches in the
  player where Apple's client does (off, any sidecar, an embedded track of a direct play), and is replanned with
  `track_change` otherwise, for one to leave a burn-in; it goes with later replans, a new attempt starts with it,
  and it is saved to `/api/v2/subtitle-prefs` (never with `show_forced_subtitles`, which would pin the profile's
  forced setting on the series). A sidecar that arrives after another load or pick is not shown, and a new load
  closes the sidecar routes of the one before.
- **Added subtitles**: Add Subtitles stores one through `SubtitlesApi` (`/api/v2/subtitles/search`, `download`,
  `upload` as a form, `detect-language`, `providers/status`, all with the profile's headers). There is no refresh
  operation, so `ShowAddedSubtitle` replans with `track_change` for the tracks that play, as the web re-reads the
  inventory; the new plan reloads the stream where the viewer is, and the stored subtitle (its `sync_key`
  `stored-{id}`, an older server's `downloaded_subtitle_id` in the URL) is picked as the viewer's, as Android selects
  a download. A replan already on its way may list it; if it does not, one more is asked for after it, then it is
  given up.
- **Marker edits** (`marker_edit`): `AccountPermissions` offers the button as Silo's web does (the permission, or
  an admin acting on the primary profile, read from the profile list once per page). Save sends only the kinds that
  changed to `PUT /api/v2/markers/files/{file_id}` (`MarkersApi`; a cleared kind as null, so a scanned marker is not
  rewritten as manual), through `ServerPlayback.SaveMarkersAsync`, which takes the server's answer as the playing
  file's markers at once: the skip pills and the seek bar follow; a marker read still on its way is dropped.
- **Subtitle look in the player**: the Appearance panel edits a `SubtitleAppearanceEditor` (shared with Settings);
  each change goes to mpv at once and is saved through `IProfileSettings` as the profile's, one save after another
  and only the latest of those waiting, clearing this device's row once when it has one. "Use fallback style"
  resets both rows and reads the effective value back. A new title reads the profile's look afresh.
- **Quality, file and speed** (8.7, 8.11): the plan's `available_qualities` (an older server's `original` rung left
  out) and `active_quality` make the menu; a pick replans with `quality_change` from where the viewer is, and later
  replans and attempts ask with it too; it is not saved, as on the web. Another file of the title starts a new
  attempt (and session) at the position, with its own automatic subtitle choice. A quality or a file the server refuses
  leaves the stream that plays, and the one asked for before stays the one later attempts ask for; a pick made while
  the stream is being replaced goes out once that is done, as an audio pick does; so does a seek, and a stream that
  broke off meanwhile reconnects once the refusal is in. The speed is mpv's `speed`, read
  from this device's `player.playback_speed` and saved there once it settles for a second (a drag passes several
  stops), or at the stop.
- **Markers** (11.1, 11.2): the playing file's intro (its single projection, as Android takes it), recaps, credits
  and previews come from the watch detail, then from a second read that names the file once its first frame
  shows (`file_id`, which makes the server fill markers it finds on demand); a failed read changes nothing.
  `MarkerPrompts` follows the position: with `playback.intro_skip_mode` `ask` the intro offers "Skip Intro" for 5 s
  of playing time (a pause stops the clock, a stall only after 1.5 s), `always` skips it and offers "Watch Intro";
  a picked, turned away (Esc) or settled intro is not offered again in that playback. Recap, credits and preview
  get a pill that stays, and skip themselves once where `playback.auto_skip_recap` or `auto_skip_credits` say so,
  as Apple's client does. The settings are read as their own batch; an unknown mode neither prompts nor skips.
- **Next episode** (10.7): the playback names its episode (series, season, number); `UpNextSource` reads the
  current season's episodes, and when none is later, those of the lowest later season that is not specials, and the
  smallest (season, episode) after this one plays next. `NextUpFlow` decides what shows: the card when playback
  crosses the credits' start (else the length less `playback.next_up_prompt_seconds`), the screen 30 s before the
  end (the web's post-roll), and at the end a 10 s countdown, when `playback.auto_play_next` is on, the viewer did
  not turn the prompt away (Cancel, Keep watching, a pause or seek during the card), and fewer than three episodes in
  a row started by themselves (`AutoPlayStreak`, Android's pass-out gate). The next episode starts as a new playback
  from 0; the saved track preferences carry the picks over. The transport's previous and next episode buttons start
  theirs from the resume point, as the web player does for the viewer; the previous one is the largest episode before
  this one in the current season that has a readable file, as the web finds it, none before the season's first.
- **Chapters** (11.3): the playing file's `chapters` from the watch detail (mpv reads none through a remux or a
  transcode), in order, empty ones left out, pictures resolved against the server; they make the Chapters menu, the
  seek bar's ticks and the chapter named in its bubble.
- **Seek-bar previews** (11.4): for a file with `trickplay_available`, the manifest is read after its first frame and
  again 5 minutes before its sheets expire (a minute after a transient failure; a 404 means none). Thumbnail `i` of
  `interval_ms` sits on sheet `i / (cols·rows)`; the seek bar keeps three sheets decoded at the bubble's width and
  loads the next one ahead. Markers carry their segment's picture for the bubble; it plays no part in their equality.
- **Settings pages** (6.6): `ProfileSettings` reads the page's keys in one batch, key by key when an older server
  refuses one it does not know (422), and leaves those out. A save writes the profile's value and, when the value
  shown came from this device's row (`source: profile_device`), clears that row, which would otherwise keep shadowing
  it; a reset clears both. "No limit" and "No preference" are resets, not stored values. Playback reads
  `playback.auto_play_next_preview` in a batch of its own; with it the next episode is offered from the preview teaser
  that runs into the credits (`MarkerSettings.AutoplayStart`, the web's resolveAutoplayMarker).
- **Test speed** (`connection_speed_v1`; the server's buffering-quality design, Connection speed test):
  `ConnectionSpeedTest` runs the accurate test only, never the automatic quick one, and sends no estimate on starts.
  It asks the advertised `bandwidth_probe_url` (resolved against the server) for `size=1000000000` through `SiloHttp`,
  so the bearer goes only beneath the server and a verified local route is taken, as media is. The capabilities that
  name it are read from the saved address: the server builds the URL from the address it was reached at, which over a
  local route is the private one, trusted only through the route. A redirect is
  followed once, by hand, through a client of its own with no credentials, no identity headers and no logging: the
  address carries a token. `BandwidthProbe` times the body: the clock starts at the first byte (abandoned when none
  comes within 10 s of asking), a ticker closes 500 ms windows on the clock so silence counts, and `SpeedTestMeter`
  (Android's, checked against the design's `probe_cases`) settles the rate over the last six windows of 10 s, or the
  whole transfer on a link too fast for one. The result goes to `bandwidth-probe/results` with `manual: true` and the
  link's transport from Windows (`INetworkLink`: Wi-Fi, mobile broadband, Ethernet by adapter type, else other); a
  cellular or metered link is not tested. A 429 holds the button's message for its `Retry-After` (30 s without one).
  A second press joins the test that runs; leaving Settings stops it, since that is the way to playback.
- **A new title plays**: mpv keeps `pause` across files and sets it itself at a file's end (`keep-open`), where the
  countdown starts the next episode from; the first load of a playback therefore unpauses, while its reloads (a
  replan, a reconnect) keep what the viewer chose.
- **While playing**: progress every 10 s, and once a pause has held for a second (`PlaybackProgress`: one
  sequence per sample, retried with the identical body). Leaving the page by any way stops the session with
  its final position and stops the engine (`PlayerViewModel.Leave`); before the session's first frame the stop
  sends 0, which the server never stores, so a quick look does not move the resume point. Closing the window
  waits up to 4 s for stops still on their way (`PlaybackStops`). An end within 30 s of the title's end
  finishes it; an earlier end is a stream that broke off.
- **Recovery**: headers the server refuses even after the proxy renewed them → the same plan reloaded where the
  viewer was, with a fresh token; a plan
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
- **Media proxy** (`MediaProxy`, the Apple client's design): an `HttpListener` on 127.0.0.1 between mpv and the
  server. Each load opens a route (`IMediaRoute`); mpv gets `http://127.0.0.1:{port}/{secret}/{route}/{the plan
  URL's path and query, byte for byte}`. The route forwards GET and HEAD with the range headers to its own stream
  only (a progressive stream: its exact URL; HLS: the playlist and what lies beside it, no dot segments), adds
  the headers per request, renews a token within 60 s of its end, and on a 401 renews it once and repeats the
  request, so mpv never sees an expiry. Redirects are not followed, cookies and the upstream's other headers
  stay behind, and a body that breaks off aborts the response so mpv sees a lost connection, not an end. The
  secret keeps other programs on the machine out; the log redactor masks it. A new load, the stop and a failure
  close the route and its requests. Before the proxy, mpv kept the headers for the whole load and would have
  sent them wherever a redirect or a playlist pointed.
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

## Trailers

A title's YouTube trailers play in Granary, in YouTube's own embedded player inside a WebView2 (`TrailerOverlay` in
the App, `TrailerOverlayViewModel` and `Granary.Core.Trailers` in Core). Silo's Android and Apple apps hand trailers to the
YouTube app or the browser; Granary does as Silo's web (`TrailerModal`) instead. Never through mpv or a downloader: that
breaks YouTube's terms.

- **Runtime.** The WebView2 runtime is the system's: the Evergreen runtime that ships with Windows, not a fixed version
  bundled with Granary. Only the SDK's loader and projection come with the Windows App SDK (THIRD_PARTY_NOTICES.md).
  Without the runtime, a trailer offers "Open on YouTube".
- **Data folder.** `WebView2` under the data directory (`AppPaths.WebView2Directory`), so it follows `GRANARY_DATA_DIR`.
  The runtime keeps its own files there (`EBWebView`). Each trailer's browser runs InPrivate, so YouTube's cookies, storage
  and cache last only as long as the trailer and are not kept in that folder. An environment is made for each trailer
  and let go with it, with `--autoplay-policy=no-user-gesture-required`, since the click that opened the trailer was in
  Granary, not in the page. Once a trailer closes, none of its `msedgewebview2` processes is left (checked in the running
  app).
- **The page.** The WebView never goes to the embed itself: without a Referer YouTube refuses it (error 153). It goes to
  `https://trailer.granary.example/player`, which Granary answers through `WebResourceRequested` (anything else on that
  host is a 404; `.example` never resolves). `TrailerPage` builds it from the validated key alone: the
  `youtube-nocookie.com` embed in an iframe with `referrerpolicy="strict-origin-when-cross-origin"`, and a script that
  speaks the IFrame Player API's postMessage channel itself, so no script of YouTube's runs in the one document that can
  message Granary. Its policy runs only that script and style (by hash) and frames only YouTube's two embed hosts; the
  page is not cached. No token, server address or title metadata goes into it. Its script and style have LF line ends
  only, whatever the checkout's: the browser hashes them that way, and a hash over CR LF would block the page.
- **Navigation.** `TrailerWebPolicy` decides, as pure functions: the WebView goes only to that page (`NavigationStarting`);
  the page's iframe loads only `https://www.youtube-nocookie.com/embed/<key>` or `https://www.youtube.com/embed/<key>`
  (the frame's own `NavigationStarting`, from `FrameCreated`); everything else is cancelled. Frames inside YouTube's player
  (its own, an ad's) are YouTube's and load as in any browser: they cannot navigate the page, open a window or reach
  Granary. Their hosts are logged at Debug. Addresses are taken only as https on the default port, without userinfo, with
  the host compared whole, and refused outright with a backslash, a space, a control or non-ASCII character.
- **New windows.** Never a WebView window: a link the viewer clicks in the player (its YouTube logo, the title) goes to the
  browser when it is https on `youtube.com`, `www.youtube.com`, `m.youtube.com` or `youtu.be`, and anything else, or
  anything the viewer did not click, is dropped.
- **Settings.** Developer tools, the default context menus and script dialogs, the status bar, zoom, browser accelerator
  keys, swipe navigation, general autofill, password saving and host objects are off. Permissions are refused, all but
  autoplay; downloads, external URI schemes and basic authentication are cancelled.
- **Messages.** In a fixed schema (`TrailerPageMessage`: ready, playing, an error code, or close; 128 characters at most,
  no other member). From the page's own origin all of them; from the page's iframe, only while it is YouTube's embed,
  close alone (`TrailerWebPolicy.FromPage`, `FromEmbed`). Anything else is ignored.
- **Keys.** Once the viewer clicks into the video, the keyboard belongs to the browser, and Granary's Esc and Back never see
  it. So `TrailerPage.CloseKeysScript` is added to every document of the WebView
  (`AddScriptToExecuteOnDocumentCreatedAsync`). Esc, the Back key, Alt+Left and the mouse's back button post
  `{"kind":"close"}`; in full screen Esc only leaves full screen. Checked with synthetic events in the page and in
  YouTube's frame; a frame can do no more with it than close the trailer.
- **States.** `TrailerOverlayViewModel`: Loading, Playing (the player answered), Unavailable with its reason (the player's
  errors 2, 5, 100, 101, 150, 153 and any other; no answer within 15 s of the page opening, or no page within 30 s of the
  start, both through `TimeProvider`; no runtime; the browser's, the page's or a frame's process gone), Closed. Closing
  closes the WebView2 once the WebView's own event that closed it has returned; that stops the video and ends its
  processes.
- **Full screen.** When the page asks for it (`ContainsFullScreenElementChanged`), the window goes full screen through its
  AppWindow presenter, as the player does (`WindowedPlacement`), and the app under the trailer is hidden meanwhile.
- **Logs.** The outcome only ("The trailer plays", "can't play in Granary: EmbedRefused"); a blocked navigation is logged
  without its address, its host only at Debug. A YouTube address carries the video's key and stays out of the log.

## Native dependency

`libmpv-2.dll` is the official mpv CI build (LGPL variant), pinned with its SHA-256 in
`native.lock.json` and fetched by `scripts/fetch-native.ps1` into `native/libmpv/` (not committed). The
upstream asset is replaced on every mpv master update, so a release must mirror the pinned archive. The pinned
asset has already been replaced upstream (its URL answers 404): until the pin points at a mirror, pass the pinned
archive with `./scripts/fetch-native.ps1 -ZipPath <zip>`, which still checks its SHA-256.
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
| Trailers in YouTube's embedded player in WebView2 | YouTube's terms allow its player only; mpv or yt-dlp would break them. The web plays them the same way. |
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
