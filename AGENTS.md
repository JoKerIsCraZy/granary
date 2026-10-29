# Granary

Granary is a native Windows client for Silo Server, the self-hosted media server. It is written in C# on .NET 10 with
WinUI 3 (Windows App SDK) and plays media with libmpv. It is an independent project, not an official Silo app.
Read [README.md](README.md) for what it does and [docs/architecture.md](docs/architecture.md) for how it is
built.

## Layers

```
src/Granary.Core     net10.0                         API client, auth, models, playback logic, view models
src/Granary.Player   net10.0                         libmpv interop and the playback engine (IPlaybackEngine)
src/Granary.App      net10.0-windows10.0.22621.0     WinUI 3: windows, pages, controls, platform services
tests/Granary.Tests  net10.0-windows                 unit tests and libmpv integration tests
```

- Dependencies point inward only: `App → Player → Core` and `App → Core`. Core has no UI dependency.
  It defines the ports the other layers implement: `IPlaybackEngine`, `IUiDispatcher`, `INavigationService`
  and `ISecretStore`.
- View models live in Core and use CommunityToolkit.Mvvm: `ObservableObject`, `[ObservableProperty]` partial
  properties and `[RelayCommand]`. Pages bind them with `x:Bind`. Code-behind only does what needs WinUI,
  such as dialogs, focus, scrolling and composition.
- Services are registered in `src/Granary.Core/CoreServiceCollectionExtensions.cs`. `src/Granary.App/App.xaml.cs`
  is the composition root.
- Every API call goes through `SiloHttp`:
  - It serializes with System.Text.Json source generation (`SiloJsonContext` and the per-area contexts such as
    `CollectionsJsonContext`), using snake_case.
  - Errors are RFC 9457 problems, raised as `SiloApiException`; branch on the status or problem code, never
    on `detail`.
  - View models classify failures with `LibraryErrors.IsExpected` and word them with `LibraryErrors.Describe`.
- Only `/api/v2` is used. [docs/api-contract.md](docs/api-contract.md) records every endpoint Granary uses;
  add to it when you use a new one.

## Where behaviour and look come from

- Silo's own clients are the reference. Read-only clones live in `.ref/` (git-ignored; see
  docs/api-contract.md).
- **The apps decide behaviour, the web decides look.** How something works (track choice, retries,
  what is saved where) follows the Android and Apple apps. How it looks and what it says follows the web app
  (`web/src`), including its exact texts.
- Comments name the source they follow, for example "as the web's `pages/Catalog.tsx`".
- [docs/ui-design.md](docs/ui-design.md) describes every screen. Update it with the UI.

## Code style

- `.editorconfig` holds the formatting:
  - file-scoped namespaces, braces always
  - `_camelCase` private fields, usings outside the namespace
- Comments and XML docs are short, plain English. They say what a member is for and why, from the user's side
  where that helps. They do not restate the code. Match the density of the surrounding code.
- Keep files focused. Split a large view model into partial files by area (for example
  `PersonalListViewModel.History.cs`).
- No hardcoded secrets. Never log tokens, tickets, credentials or request headers. Every log line passes
  `LogRedactor`, but do not rely on it.

## Building and testing

```powershell
./scripts/fetch-native.ps1                                        # libmpv into native/libmpv (git-ignored); see below
dotnet build Granary.slnx
dotnet build src/Granary.App/Granary.App.csproj -p:Platform=x64  # the app project alone
dotnet test --solution Granary.slnx
```

- Warnings are errors (`TreatWarningsAsErrors`, analyzers included). A change must build with 0 warnings.
- Package versions live in `Directory.Packages.props` (central package management).
- libmpv: `native.lock.json` pins the build by SHA-256 and downloads it from this repository's mirror release
  (`deps-libmpv-…`; upstream replaces its asset on every mpv master build). Until that release is uploaded
  (docs/release.md), pass the exact pinned archive with `-ZipPath`; the hash is still checked. Do not re-pin
  `native.lock.json` on the side: a new pin also needs a new mirror release and changes THIRD_PARTY_NOTICES.md.
  Without libmpv, builds and tests still run and the libmpv integration tests skip themselves.

Tests run on xUnit v3 with Microsoft Testing Platform. There is no mocking library: fakes are small hand-written
classes in `tests/Granary.Tests/Fakes`.

- **`ScriptedSilo`** (`tests/Granary.Tests/Profiles`) answers HTTP requests scripted by method and path. It records
  each request as a `SentRequest`: `Method`, `Uri`, `Body`, `Auth` (the `SiloAuth` level the call asked for),
  `SavedAddressOnly` and `IfMatch`.
- **`LibraryHarness`** (`tests/Granary.Tests/Browse`) sets up a ready session on profile `7` with the usual
  answers.
- **`ManualTimeProvider`** drives delays, debounces and polling. View models take a `TimeProvider` for this;
  do not use real time in a test.
- **Cancellation tokens:** pass `TestContext.Current.CancellationToken` to calls that accept one. The xUnit
  analyzer rule xUnit1051 turns a missing token into a build error.
- **Test names** state the behaviour, for example `AFailedUndo_CanBeTriedAgain`.
- **Order of work:** write the test first, then the code, and keep the whole suite green.

## Running the app

- Set `GRANARY_DATA_DIR` to an empty folder for any test run or second instance. Never run against a user's own
  `%LOCALAPPDATA%\Granary`: it holds their servers, sign-ins and device id.
- `--play <file>` plays a local file without a server. `--play-item <content id>` plays a title of the
  signed-in server.
- Drive the running app with UI Automation patterns, not simulated global mouse or keyboard input, which lands
  in whatever window is on top.
- Never take actions on a real server that change someone's data, such as requests, watch state or settings,
  unless that is the point of the check.
- `scripts/capture-window.ps1` takes a screenshot of the app window. `scripts/capture-debug-output.ps1` records
  `OutputDebugString`, where WinUI names the resource behind a runtime `XamlParseException`.
- Open every new page once in the running app. XAML that compiles can still fail when a page is created.

## Gotchas

- **x:Bind functions:** in a page's or control's own markup they are instance methods, called on the page or
  control (see the comment in `Controls/AddSubtitlesPanel.xaml.cs`). Inside a `DataTemplate`, call static methods
  on the page type, for example `views:NotificationsPage.Poster(PosterUrl)`.
- **x:Bind function limits:** give each target type its own function; one function feeding both a `bool` and a
  `Visibility` generates broken code (`Controls/AddSubtitlesPanel.xaml.cs`). Do not put an `x:Bind` function inside
  a `Run` (use separate `TextBlock`s), and do not pass an indexer into a function (`Fn(List[0].X)`): both break the
  XAML compiler.
- **UserControls made in code** do not get the page's merged dictionaries. Merge `Themes/SiloSettings.xaml`
  into their resources, as `Views/Admin/AdminDashboardView.xaml` does, or their `StaticResource` lookups fail
  at runtime.
- **Items of an ItemsRepeater** size their parts in `MeasureOverride`, from the width they are measured at, never from
  `SizeChanged`: the repeater parks recycled elements at 0×0 and lays them out again, and a size set after each arrange
  can keep layout from settling (a `LayoutCycleException`; see `MediaCardView` and `MediaCardSkeleton`). To find the
  element behind a layout cycle, set `DebugSettings.LayoutCycleTracingLevel = LayoutCycleTracingLevel.High` for a run
  and record the trace with `scripts/capture-debug-output.ps1`.
- **ClientWebSocket:** a `ReceiveAsync` cancelled through its token aborts the socket. Close it through a token
  registration and `CloseAsync` instead, as `WebSocketRealtimeSocket` (`IRealtimeSocket`, in
  `src/Granary.Core/Realtime/RealtimeSocket.cs`) does for both the playback control lane and the events lane.
- **JSON source generation:** a member missing from the JSON sets an `init` property to its default, not to its
  initializer. When absence must mean something else, use a nullable and a computed fallback (see
  `CollectionCapabilities.Artwork` and `TakesArtwork`).
- **DI lifetimes:** a transient `IAsyncDisposable` resolved from the root provider lives until exit and breaks a
  synchronous container dispose. Register a `Func<T>` factory instead (see `Func<PlaybackControlLane>`).
- **Writes with a version:** writes that carry a version send it back as `If-Match` (`SiloHttp.SendIfMatchAsync`).
  A 412 means the data changed elsewhere: read it again, never just overwrite.

## Commits and docs

- Conventional commits, one change per commit:
  - the subject says what changes for the user, for example
    `feat(notifications): Series view with search, reasons, sort and Mute`
  - types: feat, fix, perf, refactor, docs, test, chore, ci
  - the body explains the behaviour, not the diff
- Update the docs the change touches in the same commit: api-contract, ui-design, architecture, release.
- Add user-visible changes to `CHANGELOG.md` under `[Unreleased]`. A release's update notes come from it
  (docs/release.md).

## Branding

- Granary has its own name, logo and app icon.
- Silo's name and mark are used only to refer to Silo, as
  [Silo's trademark policy](https://github.com/Silo-Server/siloserver.org/blob/main/TRADEMARK.md) allows: "a client for Silo
  Server", "Connect to your Silo server".
- Write the name as **Silo**, with one capital S, never in capitals. The server software is **Silo Server**; a
  user's own instance is "your Silo server".
- Do not change Silo's brand files in `src/Granary.App/Assets/Brand`.
