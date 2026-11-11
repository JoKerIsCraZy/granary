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
  `INavigationService`, `ISecretStore`, `ISystemMediaControls` for Windows' media keys and their overlay
  (`PlayerSystemMedia` decides what they show and maps their buttons to the player's commands; the App's
  `WindowsMediaControls` drives the main window's System Media Transport Controls), `IRichPresence` for Discord's
  status (see Discord status), and `IKeepAwake`, which keeps the display on while a title loads, plays or buffers
  (`PlayerKeepAwake`, made and disposed with `PlayerSystemMedia` by the player page, follows Android's player: not while
  paused, and only the system for sound without a picture, and asks again with the player's next change when Windows
  refused; mpv's `stop-screensaver` asks Windows only from a window of its own, which composition mode has not). The
  App's `WindowsKeepAwake` holds Windows' power requests (`PowerCreateRequest`, made once per reason and reused, then
  `PowerSetRequest` and `PowerClearRequest`), as Core's `KeepAwakeRequests` lays them out: the new ones set before the
  old ones are cleared, so nothing lapses. `powercfg /requests` then shows `Granary.exe` with "Playing video" under
  DISPLAY and SYSTEM, or with "Playing audio" under EXECUTION and SYSTEM. A request belongs to its object, not to a
  thread; a lock keeps the state whole if a call came from another thread. Should Windows make no request object, it
  falls back on `SetThreadExecutionState` (`ExecutionStateFlags`), which belongs to the calling thread, the UI's.
  `IThemeAudioOutput` plays title pages' theme music (see Theme music).
  `IReleaseFiles` gives `SignedReleaseVerifier` a release's SHA256SUMS and its signature, and the package on disk to
  hash and read; Core's own `HttpReleaseFiles` implements it, and the tests fake it (docs/release.md, "Signed
  releases").
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

The panel shows the last presented picture unscaled, from its top-left corner, and mpv resizes its
buffers a little after the panel on its own thread. After a jump in size (the player minimized to its
bar or brought back, full screen, the Playing Next corner), `MpvVideoHost` therefore hides the picture
in the layout pass that gives the panel its new size, and passes the new size to mpv only once the desktop
compositor has drawn that frame (`DwmFlush`, twice): the hidden panel and mpv's pictures reach the compositor
separately, and a heavy frame could otherwise arrive after mpv's first picture at the new size. It shows the picture again once mpv has presented
after its buffer took that size, or has presented nothing for 100 ms since (paused), and after 300 ms
at the latest (`CompositionResizeGate`, which reads the buffer size and the present count from DXGI).
Small steps, as while the window's edge is dragged, are passed on at once and keep the picture.

Because there is no window, mpv cannot query the monitor: the app must pass HDR target settings and
the display refresh rate itself (Phase 3). Until it does, the output is SDR, and mpv tone-maps HDR and
Dolby Vision originals onto it. Where the GPU decodes the codec of the title's HDR files at 10 bits, Granary claims
that on direct play (`client_managed_dynamic_range_v1`, docs/api-contract.md 13.2), so the server sends HDR files
as they are instead of tone-mapping them in a transcode. `PlaybackStarter` builds the report for the files a start
may get, again when the viewer switches files in the player and after the server was reinstalled.

## Your own mpv.conf

Settings → Playback → This PC opens `mpv.conf` in the data directory's `mpv` folder (`MpvConfigFolder`,
`%LOCALAPPDATA%\Granary\mpv`), made with comments only when it is missing. mpv reads the file itself, so mpv's own
syntax holds: profiles, `profile-cond` (mpv's built-in `auto_profiles.lua`), `include`, and `~~/` for the folder
(`glsl-shaders=~~/shaders/…`). The file is the viewer's own and trusted as mpv trusts it, within the limits below.
Without it, mpv starts exactly as before: the same options in the same order (`MpvOwnOptions.Core`), `config=no`
(libmpv's default), and its log asked for after the start. An `audio-delay` in the file stays as long as the audio sync
offset is 0 and nothing in this run gave the engine another (`MpvPlaybackEngine.SetAudioDelay`); after that Granary's
value holds, 0 included.

**Start order** (`MpvPlaybackEngine.Start.cs`). Found with libmpv v0.41.0-dev (`player/main.c`, `mp_initialize`;
`player/loadfile.c`, `play_current_file`) and checked in a spike and in `MpvUserConfigTests`:

- With `config=yes` and `config-dir=<folder>`, mpv reads `mpv.conf` inside `mpv_initialize`, after every option set
  before it, so the file overrides them. `~~/` needs this: with `config=no` it resolves to nothing, so loading the file
  through `mpv_load_config_file`, `include` or the `load-config-file` command instead would lose it, and with
  `config=yes` mpv reads the file anyway.
- mpv acts on the file at once: its core thread leaves `mpv_initialize` straight into idle mode and makes the video
  output there, before any call after `mpv_initialize` can run. In the spike, `vo=gpu` in the file made a `gpu` output
  first, which setting `vo` back after the start replaced with a second one (a new swap chain); `idle=no` shut mpv down
  before the first call after the start. Setting Granary's options back after the start is therefore not enough.
- mpv applies the `pseudo-gui` profile right after the config files, inside `mpv_initialize`, over them, when
  `player-operation-mode=pseudo-gui` (an option client.h lists for setting before `mpv_initialize`). Granary puts its own
  options into that profile before the start, as config text through `mpv_load_config_file` with mpv's `memory://`.
  They win before scripts load, before the video output is made and before idle mode is decided: one `gpu-next` output
  in composition mode from the start, whatever the file says. The built-in `builtin-pseudo-gui` profile that comes with
  this mode sets only what Granary sets too (`force-window`, `idle`) or never uses (a screenshot folder).
- Three things in a file would undo that, and such a file is refused: mpv starts without it and the log says why.
  `player-operation-mode` set to anything but `pseudo-gui` turns the profile off. A `[pseudo-gui]` section of the file
  joins Granary's profile after Granary's entries (mpv adds a section's options to the profile of that name), so one
  that sets one of Granary's options to another value wins over it. And some options a file may not set anywhere, at
  the top, in any profile (`[pseudo-gui]`, `protocol.*`, `extension.*`, conditional ones) or in an include
  (`MpvOwnOptions.Refused`): mpv acts on each as soon as it is set, before any set-back. A looping profile that set
  `log-file` while a title played had mpv write its log, the stream's address among it, before Granary set it back
  (one test run in five, and once in ten live runs). These are the options that write the stream's address or its
  content out (`log-file`, `dump-stats`, `watch-later-dir`, `write-filename-in-watch-later-config`,
  `save-watch-history`, `watch-history-path`, `stream-record`, `stream-dump`, `demuxer-cache-dir`, `cache-on-disk`,
  `ao-pcm-file`, `vo-image-outdir`), send it through a proxy (`http-proxy`, `stream-lavf-o`, `demuxer-lavf-o`), hand it
  to other programs, scripts or commands (`input-ipc-server`, `input-ipc-client`, `scripts` and `script`,
  `load-scripts`, `input-commands`, `ytdl`, which runs a downloader with it), read key bindings (`input-conf`), or make
  mpv Windows' player for media files (`register`, `unregister`). A value that is Granary's own or mpv's default
  (`log-file=`, `load-scripts=no`) is no reason. Screenshots are left to the file: with no bindings, commands or
  scripts nothing can take one. The shader and ICC caches are left too: they hold compiled shaders, nothing of the
  stream.
- Granary reads the file before the start by mpv's rules (`MpvConfigText.Scan`), its includes too, the way mpv finds
  them: `~~/` is the folder, `~/` the user's home, any other relative path is opened from the working directory, and
  an include's own options count in the profile it stands in. A missing include is passed over, as mpv does. Includes
  that include each other are refused: libmpv v0.41 reads them over and over inside `mpv_initialize` and never starts
  (a test hung on it before the check). So are includes nested deeper than 8 (mpv's own limit), one Granary cannot
  read, and one it cannot follow, such as a stream URL. Anything unexpected while Granary reads the file makes mpv play
  without it: the file never stops playback. Once mpv started, Granary checks again what mpv read: the mode
  (`player-operation-mode`) and the profiles (`profile-list`; mpv's built-in ones, which set some of these options
  themselves, are passed over). Something found only then, or a check that fails, makes mpv start again without the
  file.
- Each title gets Granary's options as its own options too (`loadfile`, each value quoted by its length). mpv applies
  those after the file's `protocol.*` and `extension.*` profiles and before it opens the stream: a third line, should
  a profile that sets one reach mpv past both checks.
- Profiles that apply while mpv plays (`profile-cond`, or one applied by name) can still set one of the options Granary
  keeps that a file may set (`vo`, `gpu-api`, `gpu-context`, `d3d11-output-mode`, `keep-open`, `idle`, `osc`, `sub-auto`
  and the like). The engine observes those while mpv runs with a file and sets each back as mpv reports it
  (`KeepsOwn`), comparing the value as mpv writes it without regard to case. Between the profile and the set-back, mpv plays a moment with the
  file's value; it never holds server credentials, only the loopback address with the media proxy's secret (Server
  playback, "Media proxy"). `d3d11-composition-size` is kept at the start only, as the engine changes it itself.
- The viewer's kept volume and mute (Server playback, "Volume") are set back the same way once Granary has set them
  (`KeepsViewersSound`, `MpvPlaybackEngine.Volume.cs`): a profile's `volume=` or `mute=` as a file opens or while it
  plays gives way to them. That is the viewer's choice, not a protection: after `SetBackLimit` set-backs within a title
  the profile has its way for the rest of it, and the file stays in use. A notice older than Granary's latest value
  (what mpv holds now is Granary's) is passed over rather than set back. Before Granary sets either, a profile's stands.
- The protection fails closed (`MpvPlaybackEngine.FailClosed.cs`). A profile whose condition follows the option itself
  would set it again after every set-back; after five set-backs of one option within one title (`SetBackLimit`; the
  count starts again with each load, so a profile that changes it once per title never gets there), at the next change, or
  when mpv does not take Granary's value back, Granary stops the file at once, so nothing more is read or written under
  the file's value, and starts a new mpv without the file, the same start as when the file is refused, before it
  closes the one before, so the graphics device stays in use all along. The log says
  `mpv.conf keeps changing log-file; Granary stopped and plays without your mpv.conf`. The title that played ends as a
  failure (`PlaybackEndReason.Error`): a server playback reconnects by itself and loads it into the new mpv, and the
  player of a local file shows the failure with Retry. The engine's own settings carry over (volume, mute, speed,
  audio delay, subtitle look and delay, fill, the picture's size), and a new swap chain reaches `MpvVideoHost` through
  `SwapChainChanged`. A call that still reaches the old one finds it closed and does nothing (`MpvClient`). The new
  mpv starts on the thread pool, so a load can come between the stop and that start: the load then makes the start
  itself, under `_restartGate`, and its title plays in the new mpv (`StartWithoutConfig`, once per stop, counted by
  `_failClosedStops`); the start after the stop finds it done, raises the stopped title's failure, and leaves the new
  title's state alone. Until the new mpv runs (`_failingClosed`), the stopped file's own end is not passed on.
  Should even mpv without the file not start, the mpv before stays (`ReplaceMpv`, below), still holding the rejected
  file, the title stays stopped, and the log says `A new mpv did not start without mpv.conf; the one before plays on`.
  `_failingClosed` stays set then: no title goes into that mpv. The next load tries the plain start again, and when it
  fails too, that title ends as a failure without being loaded (`EndWithoutPlaying`).
- Compiled shaders are cached in `mpv\cache` (`gpu-shader-cache-dir`, `icc-cache-dir`), not in mpv's own folder under
  `%LOCALAPPDATA%\mpv`, where mpv puts them once `config=yes`.

**Granary's own options.** Always, and kept over the file: the video in Granary's window (`vo=gpu-next`, `gpu-api` and
`gpu-context` `d3d11`, `d3d11-output-mode=composition`, `d3d11-composition-size`, `force-window`), the player Granary
drives (`keep-open`, `idle`, `input-default-bindings`, `input-vo-keyboard`, `osc`, `ytdl`, `sub-auto`,
`rebase-start-time`, `media-controls`), no watch-later files (`resume-playback`, `save-position-on-quit`), and room for
the breaks of a long title that the media proxy gives up on (`curl-max-retries=20`; Server playback, "Media proxy"). With a
file also (`MpvOwnOptions.Guarded`; they are mpv's defaults, so without a file nothing could change them), behind the
refusal above: the stream's address stays out of mpv's own files and programs (`save-watch-history`, `watch-later-dir`,
`write-filename-in-watch-later-config`, `log-file`, `dump-stats`, `stream-record`, `stream-dump`, `input-ipc-server`),
goes through no proxy (`http-proxy`, `stream-lavf-o`, `demuxer-lavf-o`), mpv runs nothing of the file's (`load-scripts`,
`scripts`, `input-commands`) and reads no key bindings (`input-conf` is an empty one of Granary's, so the folder's
`input.conf` is not read), and it never makes itself Windows' player for media files (`register`, `unregister`). These
are set one by one, and one this mpv does not know is logged and left out, so an mpv that drops or renames one still
starts. All of them exist in v0.41.0-dev (`ThisMpv_KnowsEveryOptionGranaryKeepsWithAFile`). The server's track choice
and start position go with each title (`loadfile` options), over the file's own. Everything else is the file's to
change, `hwdec` included.

**The log** says once per start which file mpv read (`mpv started with …`), names the options of Granary's that the file
sets (`mpv.conf sets vo; Granary keeps its own`, read from the file and its includes by mpv's rules for option names),
and passes on mpv's own messages about the file (an unknown option, a shader it cannot open),
which it asks for before `mpv_initialize`. When mpv does not start with the file, the log says whether mpv refused the
file (an option that only lists something, such as `audio-device=help`, with mpv's own words) or one of Granary's own
options for it, and mpv starts without the file. A folder named `mpv.conf` is said and passed over. The engine also
reports the outcome for the run to `IMpvConfigStatus` (a Core port, one for the app): used, used with options Granary
kept, not used, or stopped during playback, with the log's reason, which names options and included file names but no
stream address and no folder beyond the mpv folder. Settings → Playback → This PC shows it (docs/ui-design.md).

**Granary's editor** (Settings → Playback → This PC → Edit, `MpvConfigEditorViewModel`) edits the real file: it reads
`mpv.conf` as it is (or the template, written only on Save) and writes the text back whole through
`MpvConfigFolder.WriteConfig`, beside the file first under a name of its own and then moved over it, so a failed write
leaves the old file. Only a file that is not there counts as new: one that cannot be read (`File.Exists` says false for a
file Windows does not let Granary look at) leaves nothing to edit, so Save can never replace it. So does one that is not
strictly UTF-8, or that holds a NUL, as UTF-16 without a byte order mark does: decoded and saved, it would come back
damaged, so the editor sends the viewer to Open mpv.conf. The text is read-only while a save runs. A
text box hands lines back with a lone carriage return, which mpv would read as one line, so a save puts back the line
breaks the file had (CRLF for a new one), and line breaks alone are no change. The check under the text is the
engine's own pre-scan, behind the Core port `IMpvConfigCheck` (`MpvConfigCheck` in the player): `MpvConfigText` with
the same option lists and `Refusal` as a start, on the text alone, so an `include` is checked when mpv starts. It
never stops a save. Each save tells `MpvConfigChanges`, which the This PC row reads to say it applies to the next title
(below).

**Presets** (`MpvPresets`) are plain files, `mpv\presets\<name>.conf`, so the viewer can see and back them up. A typed
name is made a file name first (`SafeName`: no path separators or other characters Windows refuses, no spaces or dots at
the ends, at most 60 characters, no device name such as `CON` or `COM1`); every read, write and delete takes only a
name that is one file name in that folder, so no name reaches another path. Saving one copies mpv.conf as it is on
disk; loading one copies it to mpv.conf byte for byte, a change like a save (`MpvPresets.Active.cs`). The
first load keeps the viewer's own mpv.conf byte for byte in `presets\.before-preset.conf`, written whole like the rest,
or marks that there was none (`.before-preset.none`); later loads keep that first backup, so Unload always returns to
the file from before any preset, or removes mpv.conf. `presets\.active.json` holds the preset loaded last and the SHA-256
of its text as written; an mpv.conf with another hash shows the preset as "(changed)". Deleting the preset's file leaves both, and
Unload works from the backup. Files starting with a dot are Granary's own: never listed, never a preset's name.
`.active.json` is what makes a backup or a mark count: left without it, they are from a session that could not remove
them, and the next load replaces them with the viewer's file of now, or fails if it cannot. A load writes `.active.json`
first and replaces mpv.conf last, in one move: until then the viewer's file is in place, and after it nothing is left to
fail. One that fails takes the name back and drops a backup it just made; should the name stay, so does the backup, so
Unload stays true to what is there. Once mpv.conf is back Unload removes the backup and `.active.json`, each whatever becomes of the other: a
backup left behind is not trusted, so only `.active.json` staying is no success (`NotCleanedUp`), and Unload stays offered to finish.

**When it applies**: the engine is one libmpv instance for the whole run (`AddGranaryPlayer`), made when the player first
opens, and mpv reads the file only in `mpv_initialize`. Reading it again before a title (`load-config-file`) would add
changed options but never undo one taken out or the file deleted, so a change applies when a new mpv starts, which the
next title does by itself.

**The next title** (`MpvPlaybackEngine.NextTitle.cs`, `MpvConfigChanges`). Every load compares what mpv would read with
what it last tried, and replaces mpv only when they differ. That holds for any change, made in Granary or outside it.
- `Load` hands out the load's id and its snapshot at once and queues the rest on one task chain (`_opens`), off the
  caller's thread, which is often the UI's: titles reach mpv in the order they were loaded, and one that a newer load or
  a `Stop` overtook does not open at all. The chain reads the files first, outside any gate, so a slow disk holds up that
  title and nothing else.
- What is compared is a fingerprint (`ReadConfigFingerprint`): the SHA-256 of `mpv.conf` and of the includes the pre-scan
  follows (`MpvConfigText.Scan`), each file's path and text in the order they are read, so a file that changed, appeared
  or went counts, whether Granary or another editor did it. Shaders are not in it. Writing the same text again is no
  change. Files that are not UTF-8 count byte by byte (`MpvConfigFolder.ReadForCheck`), and one that cannot be read is a
  state of its own.
- Unchanged, the load only sends `loadfile`: the same mpv, the same swap chain, nothing added to the start but the read.
  Changed, `ApplyChangedConfig` replaces the mpv through `ReplaceMpv`, which the fail-closed stop uses too: under
  `_restartGate`, the old mpv is detached, a new one starts as at any start (`Start`: the pre-scan, the pseudo-GUI profile
  with Granary's options, the check after the start, `NotUsed` when refused), the engine's settings are set again
  (`RestoreGlobals`: volume, mute, speed, fill, subtitles, `sub-pos`, and the picture's size now), and only then is the
  old one closed, so the graphics device stays in use throughout. mpv's pause carries over as it does across files. The
  window gets the new swap chain (`RefreshSwapChain`, so `SwapChainChanged`). The engine stops offering the old one as its
mpv closes: where no panel holds it, the new one can get its address, and that still counts as new. A call that still holds the old `MpvClient`
  finds it closed and does nothing. The start is a new run for Settings: `_stoppedThisRun` is cleared and the state
  reported afresh, `None` when there is no file any more, so the status line under the row says how the file fared.
- A title that plays is never cut: nothing starts a new mpv but a load, not a save, a stop or the player closing, and a
  load replaces the file that plays anyway. A reconnect or a switch of quality is a load too, so a change made meanwhile
  applies there, not only with another title.
- No retry storms: `_triedConfig` holds the fingerprint of the last try, set before the try, whatever came of it. A file
  that was refused, that Granary stopped using (fail closed) or that a new mpv failed to start for is not tried again:
  later titles play as they are, until the fingerprint changes.
- A start that fails (libmpv makes no new mpv, or anything after it fails) keeps the mpv before as it was, its handlers
  and Granary's options for it and the reported state too, and closes whatever the new start made (`ReplaceMpv`, for a
  change and the fail-closed stop alike). For a change the old mpv plays the title, the log says so, and Settings shows
  `MpvConfigUse.NotApplied`.
- Between the load and `loadfile` the file before still plays, so what mpv reports then is not the new title's:
  `_fileBefore` keeps its position, end, failure and refusal from reaching the new load's state. It ends just before
  `loadfile`, as the new file can fail at once. A pause asked for meanwhile (`_pauseWaits`) is sent right after
  `loadfile`: the players unpause right after a load, and a file held at its end pauses itself again until the next file
  takes over. A `Stop` that overtakes the open sends `stop` again once it is done, as it may have reached mpv first. A
  fail-closed stop of the file before leaves the waiting title alone: it opens in the mpv without the file. So does a stop
  of a new mpv whose changed file goes too far as it starts. `OpenFile` looks for the stop's mark under the lock under
  which `FailClosed`, after setting that mark, looks at `_fileBefore`: the stop either finds the title waiting, and the
  title goes to the mpv without the file, or finds it on its way to mpv and ends it as a failure.
- The engine's settings go through `_globalsGate`, whose holder also sets them on mpv, so a change while mpv starts again
  reaches that mpv whichever comes first, and the latest value stays, on the new mpv or on the old one when the start
  fails. A new mpv says its own volume before it gets the viewer's, so `PlayerViewModel` takes a volume it is shown by
  the engine as no change of the viewer's.
- `MpvConfigChanges` (Core, on the UI thread) holds what the row says: `IsPending` from a change made in Granary until a
  title has started with it. The engine reports `TitleOpening` before a title reads the files and `TitleOpened` once it
  went to mpv (`IMpvConfigStatus`, any thread), so a save after the read stays pending for the title after. Before the
  player first opens there is no engine: the first title reads the file anyway, and clears the line the same way
  (`MpvConfigChangesTests`, and `MpvConfigNextTitleTests` with libmpv: the new file's options in the next title, the
  settings carried over, no new mpv or swap chain without a change, a title never cut, a stop that starts nothing, a
  changed or new include, a removed file, no second try of a refused, stopped or failed file until it changes, a failed
  start keeping the mpv before, a load on a slow disk, the order of titles, a pause and a stop while a title waits, the
  file before ending meanwhile, the latest settings on whichever mpv stays, and no handles left after twenty changes).

## Threading

- libmpv events arrive on the engine's event thread; `IPlaybackEngine` raises its events there.
- `Load` returns at once: a task chain gives the titles to mpv in turn, off the caller's thread, after reading what mpv
  would read and starting a new mpv if it changed ("Your own mpv.conf", "The next title"). A failure that ends a title
  before mpv has it, and the window's new swap chain, come from that chain's thread.
- View models marshal to the UI thread through `IUiDispatcher`; they never block on it.
- `MpvVideoHost` re-enters the UI thread through its `DispatcherQueue` when the swap chain changes.
- Position updates are throttled to 4 per second; state changes are reported immediately.
- What mpv's cache holds (`demuxer-cache-state`) is observed without its value: the engine reads the property's
  string form (the node as compact JSON) at most 4 times a second and once more after the last change
  (`TrailingThrottle`, always on a timer's thread, so mpv's event thread never waits for the read; a failure is
  logged), takes its `seekable-ranges` (`MpvCacheState`, which reads anything else as no ranges) and reports them in
  `PlaybackSnapshot.Buffered`, in the time of `time-pos` (mpv applies the same offset to both). A load empties them
  at once, and a read of the file before is dropped by its load id. While `keep-open` holds the file at its end, its
  ranges stay, as on the web; they clear when the file ends (a stop, a failure or mpv's own end of the file), whose
  idle snapshot holds none. `PlayerViewModel.Buffered` moves them into the title's time with the timeline offset, as
  the position, and keeps them within the title's length. It holds them back once the plan's offset differs from the
  one the load began with: the plan is taken before its stream loads, and the engine plays the stream before
  meanwhile.
- The engine raises `StateChanged` from mpv's event thread, from the cache reads on a timer's thread and from the
  caller's thread of `Load`, each time after leaving its lock, so a snapshot can overtake another on the way. Every
  snapshot it raises is numbered under the lock (`PlaybackSnapshot.Sequence`), and its consumers (`PlayerViewModel`,
  `ServerPlayback`) drop one numbered below the newest they took: a cache read from before a stop never brings the
  stopped file back, and the position never goes back.
- Once mpv has opened a file, the engine reads whether a picture plays: a selected video track
  (`current-tracks/video`) that is not an audio file's cover (`albumart`). Without one, `PlaybackSnapshot.IsAudioOnly`
  is set, and `PlayerKeepAwake` keeps only the system awake. It is read again whenever mpv says its tracks or their
  choice changed (the property is observed without a value), so a stream that names its picture late, or a track
  switched on or off, counts too. A load clears it, so a title still loading counts as having a picture. The event and
  the tracks are read outside the lock, so what they say is kept only for the load that was playing when mpv said it
  (`MpvPlaybackEngine.Opened`, `Started`, `Retracked`): a newer load meanwhile is neither open nor started yet, and keeps
  its own state.

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
- No repeater template has a Granary control as its root: the skeleton, the rows and the credit cards sit in a plain
  `Grid`. WinUI ties each element a repeater makes to its template (`RecyclePool.OriginTemplate`), the template to its
  recycle pool and the pool to the repeater, so the walk from a Granary control at the root took in the whole repeater.
  Placeholders never come back once their titles arrive, so each grid kept a pool of skeletons, and each of them walked
  the grid's thousands of references: 1.15 of 1.6 million references per collection. Guarded by `TemplateRootTests`.
- More Episodes' Now Viewing mark (`MediaCard.IsCurrent`) costs the other cards nothing: its ring (the artwork's hairline
  in the accent and one more pixel inside it, so the artwork keeps its layout) and its pill are made (`x:Load`) for the
  first marked card only, and its ping is one composition animation that stops while the card is unloaded or parked for
  reuse (the row's `ElementClearing`). The row centres that card once its list is laid out and the scroller's range
  reaches it (`RowCentring`), following layout for at most 20 passes, so a row that stays hidden stops listening. A
  movie's This film mark in its box set's row (`MediaCard.CurrentMark`) is made the same way, by its own `x:Load` element,
  and does not ping.
- A Continue Watching card's New Season pill (`MediaCard.CaptionBadge`) costs the other cards nothing: its line, a caption
  text of its own with the pill after it, is made (`x:Load`) for the first card that has one and stands in for the plain
  caption while it shows; a recycled card without one shows the plain caption again.
- A hidden episode still (`MediaCard.BlursImage`, the profile's spoiler switches) costs the other cards nothing and itself
  less than a picture: its image is made (`x:Load`) for the first card that shows one, and it is a pre-blurred decode
  rather than an effect (Win2D, which a `GaussianBlurEffect` brush needs, is not in the app). `SpoilerArtwork` decodes the
  cached bytes an eighth as wide as the card shows them, on the thread pool, blurs them there with a Gaussian of 3 of those
  pixels, edges extended (`SpoilerBlur`: the web's 24 px at the card's size), and the card stretches the 30-odd pixels
  over its artwork, a tenth larger as the web's `scale-110`. The sharp picture is never decoded for it, nor shown when
  the blur fails: the thumbhash stays. The latest 48 blurs are kept by picture and width (`BlurredStills`), so a card
  shown again only makes its bitmap. Home's hero and banner take the same path for a hidden backdrop
  (`BrowseHeroTitle.BlursBackdrop`, `BackdropLayer`): at the hero's width the decode stops at 128 px (a 1,100 px backdrop
  is blurred by 2.8 of those pixels), stretched over the backdrop's box without the card's growth, and it fades in over
  the thumbhash as a sharp backdrop would; a wider window does not make it again (`BrowseHeroLayout.StepFor`). The hero
  takes a title whose blur changed as a new picture, so a switch fades the backdrop over while the logo stays; a picture
  that becomes hidden is let go at once rather than faded over. Until the profile's switches are read
  (`EpisodeSpoilerState.Loading`), Home makes the banner's and the hero's titles as if images were hidden
  (`HomeViewModel.TopSpoilers`), and it looks at the settings' state on the UI thread's next turn, after the switches the
  read brought, so a hidden still never shows sharp in between.
- Timers tick through `WeakTick`: a `DispatcherQueueTimer` is not a XAML object, so a Tick handler holding its control
  was a cycle the collector cannot see through. A grid's timer kept its page (through the page's scroller) after the
  frame let it go: with a small page cache, nine copies of a list page were alive. Every control with a timer, the
  player page's too, follows the same rule, guarded by `TimerTickTests`.
- The full collections XAML asks for come from WinUI itself: whenever the process's working set has grown 50 MB, and
  when XAML's own memory stops changing (`DisconnectUnusedReferenceSources`, microsoft-ui-xaml#12113); CsWinRT's memory
  pressure (1,000 bytes per wrapper, CsWinRT#2594) starts more. An app cannot turn them off, so each one has to be cheap.
  Nearly all of a pause is the walk before the collection starts, and it grows with the XAML objects .NET holds: about
  25 per card (named parts, handlers), some 26,000 at a thousand cards.

Measured at 1920 px with compact posters, two runs each: share of time the UI thread stalled over 50 ms in fast
sidebar switches 32 % to 9 %, paging down a 5,215-title grid 78 % to 5 % (19 to 125 frames a second), paging back up
58 % to 3 %; GC pauses over the runs 4.1 s to 1.3 s, memory 760 to 565 MB. Maximized on a 3840 px ultrawide it is
not smooth yet: fast switches stall 81 % to 61 %, paging the grid 95 % to 89 %, GC pauses 50 s to 19 s over a run,
memory 1.6 to 1.05 GB, and single gen2 pauses that XAML starts (DisconnectUnusedReferenceSources) still take up to
5.5 s: the live cards of the cached pages are what each collection walks. With the plain template roots and the weak
timer ticks, a minute of fast switches and scrolling, maximized on the ultrawide: UI thread silent over 0.7 s 8 times to
4, the longest GC pause 2.8 to 1.9 s, GC pauses 20.8 to 13.3 s.

## Server playback

A title plays through one `ServerPlayback` (Granary.Core) per start of the player page; the page's
`PlayerViewModel` shows its state in the title's own time and passes seeks to it. The protocol is in
[api-contract.md](api-contract.md), sections 8 to 10.

- **Before the plan** (`PlaybackStarter`): watch detail, the file played last (else the first whole file), the
  start position (the resume point, minus this PC's Rewind on resume from 30 s on as Android rewinds (`ResumeRewind`,
  read at each start, 0 by default), 0 to start over, sent explicitly; restarts and file switches keep the exact
  position), the profile's
  quality and bitrate cap (`PlaybackPreferencesReader`), the server's capabilities, and the device report:
  `MpvDeviceProbe` reads the GPU's D3D11VA decoder profiles, and logs them once; HEVC, VP9 and AV1 count only with
  hardware, unless this PC's choice of direct play without hardware decoding (`IPlaybackDevicePreferences`, read once when
  the title starts and kept through its file switches and new attempts) adds them for direct play and claims HDR tone
  mapping; HLS keeps the list without them. Each start and file switch logs what the report declares, and each plan the
  server's reason (`ServerPlayback.Diagnostics.cs`).
- **The detail page's pickers** (`PrePlayPickers`, `ItemDetailViewModel.PrePlay`): the Version, Audio and Subs of a
  movie or episode, shown from the catalog item with the same rules the start applies (`ChooseVersion`,
  `SubtitleAutoSelection`). Play passes the viewer's picks on as the `PlayRequest`'s `PrePlayChoice`; the starter takes
  its file (pinned with `allow_alternate_versions: false` when the version itself was picked, a Watch Party's
  `PinnedFileId` stays apart), starts with its audio as the version's `EffectiveAudioTrackIndex` and its subtitle as
  `SubtitlePick`, the same places the player's own picks go. Audio and subtitle picks are saved through
  `TrackChoiceWrites`, one app-wide queue in pick order that the starter settles before it reads the watch detail. A
  queued save belongs to the server and profile active when it was made: it goes out only while they still are, bound
  to that profile so the authentication handler sends it as no other (`SiloRequestOptions.Profile`), and a switch drops
  the old owner's saves, cancelling the one on its way. A Watch Party's start does not wait for the queue:
  `PrepareAsync` takes the `PlayRequest` and knows a room by it. A page reads its own item again once its own saves are
  done and takes only the newest answer, and a failed save puts its picker back on the choice the server kept; a menu
  fills its rows as it opens, so a read meanwhile does not rebuild an open one.
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
- **Audio delay** (6.6): mpv's `audio-delay`, positive for later sound as Android's `DelayAudioProcessor` has it, from
  this device's `player.audio_sync_ms` (±5 s). `AudioDelay` is one for the app: the playback reads the value in a batch
  of its own, so a server that refuses the key keeps the other settings and plays without a delay (no row, no Sound); it
  takes the value unless a change made here is still on its way, keeps the one this run knows when the read failed if it
  is the acting profile's on this server (`AudioDelay` remembers whose value it holds) and plays with none otherwise,
  sets it at the start, and follows `Changed` until its stop, as Android's `SiloPlaybackService` does. Settings →
  Playback → Sound and the player's settings menu change it (a local file starts with the delay as far as this run knows
  it and follows a change made in Settings, and a step there goes only to the engine). A change belongs to the server
  and profile acting when it was made: it is saved at `profile_device` once it settles for a second, or as the window
  closes (`PlaybackStops`, which waits within its budget for every profile's saves still on their way,
  `SaveAllNowAsync`), to that server and bound to that profile (`SettingsApi.SetValueAsProfileAsync`), so a server
  switched to meanwhile does not lose it (the one before stays signed in) and a profile its server no longer acts as is
  never written for the new one. Saves of one profile go one after another, so the latest value is the one its server
  keeps; another profile's never wait for them, so a save that hangs on one server does not hold back the profile acting
  now. Switch profile and Sign out in the sidebar save a waiting change first, waiting 2 s at most; a save that fails or
  does not answer does not stop them. A timer that was due as a later change came saves nothing; a failed save is logged
  and the value holds for this run; a read that answers after a change made here, once another profile acts, after a
  newer read was taken or after a save of that profile went through, is dropped, as its answer is older than the value
  held. A title's read, which goes with its other playback settings, is numbered before they go out
  (`AudioDelay.StartRead`) and kept to the same rule, so a save done meanwhile is what the title plays. mpv moves only
  the sound: subtitles keep to the picture, so the subtitle delay and subtitle sync, both timed against the picture, are
  untouched.
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
  not turn the prompt away (Cancel, Keep watching, a pause or seek during the card), and fewer episodes in a row started
  by themselves than Settings' Still watching prompt allows (`AutoPlayStreak`, Android's pass-out gate; 3 by default,
  Unlimited never holds back), and the sleep timer does not hold it (`NextUpFlow.Hold`). The next episode starts as a new playback
  from 0; the saved track preferences carry the picks over. The transport's previous and next episode buttons start
  theirs from the resume point, as the web player does for the viewer, and exactly there, never rewound, as Android
  changes episode (`PlayRequest.ResumeWithoutRewind`); the previous one is the largest episode before
  this one in the current season that has a readable file, as the web finds it, none before the season's first.
- **Volume** (`PlayerViewModel.Volume.cs`; the web's `player-volume` and `player-muted` in localStorage): this PC keeps the
  viewer's volume and mute (`IPlaybackDevicePreferences.Volume`, `volume` and `muted` in `playback-device.json`), and
  `PlayAsync` sets both on the engine as each title starts, a Watch Party's and a direct file included, since mpv keeps
  them across files but not past a start of Granary. Only the viewer's changes count (the slider, M, the volume keys, the
  minimized bar's): a volume the engine reports by itself, a new mpv's own or an administrator's `set_volume`
  (`ServerPlayback.Control.cs`), plays but is not kept, and Mute keeps the viewer's own level with it. The room of a Watch
  Party never changes either. Who set the value decides, not a comparison with the engine's: a value shown from an engine
  snapshot (`ShowVolume`) is the engine's, any other the viewer's, and it always goes to the engine, with an unmute when
  a mute shows or the engine has one, unless an unmute is on its way. mpv reports a volume or mute a moment after it was
  set, so what was asked of it (a change, a title's start) shows until mpv reports it, or for a second at most after it
  was asked (`RequestHolds`, for the volume and the mute each, so the slider moving keeps no mute waiting): a snapshot
  taken meanwhile does not move the slider back, and the next Mute toggles what shows, so quick presses take turns. A first
  Mute without a kept volume keeps the level the engine plays at. A change is saved once it has settled for 500 ms on
  the player's `TimeProvider`, and at once by `Leave`, which closing the player, the window and `Dispose` all go
  through. Set after mpv read the viewer's mpv.conf, and kept against its profiles ("Your own mpv.conf"), a kept volume
  and mute win over a `volume=` or `mute=` there; without a kept one nothing is set, so mpv.conf keeps its say. A
  `volume` that is not a number or a `muted` that is not true or false in the file only loses itself.
- **Sleep timer** (`PlayerViewModel.SleepTimer.cs`; the apps' `SleepTimer` and `SleepTimerController`, the web's
  `SleepTimerMenu`): a duration counts on the player's `TimeProvider` once a second, whatever plays, pauses or loads
  meanwhile, so it runs on into the next episode, and then pauses what plays, with no fade (a title already paused is left
  as it is). Run out while the next title loads, it pauses that title once it plays; at an episode's end, it stops the
  countdown to the next one; after a failure it just ends. In a Watch Party
  the viewer leaves the room instead, as Apple's player does; a room taking over, and leaving the player, end the timer.
  End of episode holds the next episode back (`NextUpFlow.Hold`: the card counts nothing and the end waits, as with
  auto-play off) for whatever plays when the end comes, and ends with that end; once a title has ended the menu does not
  offer it. The page hands the menu its rows as it opens and keeps them while it is open (as `DetailHero`'s pickers), so a
  change under it cannot take the focus out. The menu opens on this device's
  `player.sleep_timer_default_minutes` (read with the playback's settings, in a batch of its own); a duration started is
  saved there, as Android keeps the last one (`ServerPlayback.SetSleepTimerDefault`, `profile_device`). Player buttons
  can hide the button (below): `PlayerViewModel.ShowsSleepTimer` goes false, a running timer ends as Turn off ends it,
  and a hold the timer left after the end lets go (`NextUpFlow.Hold(false)` then starts the countdown). A tick reads the
  latest bar the player was told of, so one that waits behind a change from another thread does not run out.
- **Player buttons** (`PlayerBar`, `PlayerButtonsLayout`, `PlayerViewModel.Buttons.cs`; Granary's own, Silo's clients
  have no such editor): which of the eleven buttons on the right of the bar may show, and in which order, for each server
  and profile on this PC (`player-buttons.json`, by "server id/profile id", as `SidebarLayout` keeps the sidebar; the
  server keeps nothing). A `PlayerBar` is immutable: every `PlayerButton` in the profile's order and the hidden ones. A
  profile's order names buttons by id (`volume`, `sleep_timer`, `audio`, `subtitles`, `chapters`, `settings`,
  `edit_markers`, `playback_info`, `mini_view`, `fill`, `full_screen`); an id this Granary does not know is left out, and a
  button the order lacks, one an update added, takes its own place, after the one before it in the bar's own order, and
  shows. The order is written only when it is not the bar's own. A file that cannot be read shows today's bar and is
  kept as `player-buttons.json.bak` before the next write; when that copy fails, nothing writes over the file in that
  run, and changes hold until Granary closes. A change shows at once and its save follows (one at a time,
  each writing the layouts as they are then, so the last change is what stays; a failed one is logged and the change
  holds for the run), so the editor's Move takes the next press at once, as the sidebar editor's does. This PC's Sleep
  timer switch from before the editor (`IPlaybackDevicePreferences.SleepTimerSwitchOff`, `sleep_timer_button: false` in
  `playback-device.json`) is taken over once, while there is no file yet: the file is written at once with
  `start_hidden: ["sleep_timer"]`, the bar of every profile without one of its own, and once that is on disk the switch
  is dropped (`ForgetSleepTimerSwitchAsync`), so a file deleted later does not hide the Sleep timer again; a write that
  failed leaves it for the next start, and one left beside an existing file is dropped then. A change gives the profile
  a bar of its own, and Reset gives it today's. The player reads `IPlayerButtonsLayout.Current` as it is made
  and follows `Changed` until it is disposed, so the minimized bar does too: a change on the UI thread applies at once,
  so work already queued there, such as the title's end, finds a hidden Sleep timer off; one from another thread is
  posted, and does nothing once the player is disposed. The player keeps the latest bar it was told of as well, on the
  telling thread, and a sleep tick reads that, so one queued ahead of a hide from another thread does not run out. Each change carries its own bar, so hidden and shown again before
  the UI thread gets to them still end the timer. Another profile acting raises `Changed` too. The page arranges its
  buttons in `PlayerBar.Order` (`PlayerPage.ArrangeBarButtons`) and shows each one while the bar does and, for one that
  needs something, while there is that (`ShowsButton`); a menu goes with its hidden button. A hidden button is off: the
  page asks `PlayerBar.AllowsKey` before a key of one (`PlayerKey`: M, the volume keys, C, the chapter keys, P, F and a
  double click; the minimized bar keeps its own Mute and the volume keys), Down opens `PlayerBar.MenuOnDown`, the first
  of the audio, subtitle and settings menus that shows, and the view model's own commands check `Bar` too (C, the chapter
  keys, opening Playback info, filling, opening the marker panel). Hidden, Playback info closes and Fill screen ends,
  as the Sleep timer does; an open marker panel stays with its draft. F11 is the window's (`MainWindow.FullScreen.cs`)
  and always works.
- **Chapters** (11.3): the playing file's `chapters` from the watch detail (mpv reads none through a remux or a
  transcode), in order, empty ones left out, pictures resolved against the server; they make the Chapters menu, the
  seek bar's ticks and the chapter named in its bubble, and Ctrl+Left and Ctrl+Right go to their starts (`ChapterJump`,
  a pure function of the chapters and the position).
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
- **Stalls** (`ServerPlayback.Stall.cs`): a stream that waits for data (mpv's `paused-for-cache`) for 90 s at one
  position, with what mpv holds ahead growing by no more than half a second of the title, while the viewer wants to
  play is stopped and reconnected like a lost connection, as Android gives a stalled load 90 s. A slow link whose
  cache still grows (a remote server, a high bitrate, a long `cache-pause-wait`) is left to fill it, as Apple's
  detector counts bytes, not time. Not while the viewer (or a Watch Party's room) has paused, also with a pause
  asked that mpv has not shown yet. The 90 s are above the server's longest hold of a request (a playlist 30 s, a
  segment 12 s plus 30 s after a seek; docs/api-contract.md, 9.5) and the media proxy's 60 s, after which the proxy
  resumes the body, or mpv asks again by itself, and the stream usually recovers in place. Buffering's start and end
  are logged with the position and what mpv holds ahead.
- **Play intent across reloads**: a reload (a replan, a reconnect) plays or pauses as last asked of the engine
  (`IPlaybackEngine.IsPauseAsked`: the viewer, a Watch Party's room, the control lane), as Apple's reconnect
  carries the viewer's play intent. A stream that broke off plays to the end of the data mpv had and mpv pauses
  itself there (keep-open, `PlaybackSnapshot.IsAtEnd`). Nobody asked for that pause, so the stream comes back
  playing; a pause asked meanwhile, also by a room while the stream was down, stays. The engine applies the last
  ask in one step with the asks (`ApplyAskedPause`), so one made in that moment is never undone by an older one.
- **One recovery at a time**: the server refuses a second replan while one is open, so what happens meanwhile
  (the replaced stream's end, a progress answer) is dropped or comes up again once the new stream plays, and a
  seek waits for that stream. Engine states and ends carry the id of their load (`PlaybackSnapshot.LoadId`):
  one raised before a reload can arrive after it and is ignored. A failure stops the engine; after the stop the
  playback leaves the engine alone, which may already play the next title.
- **Plan change** (`IServerPlayback.PlanChange`, the web's `replanning || replacing`): a recovery that replaces the
  stream that plays names what for (quality, version, audio, subtitles, seek, restart) as it starts; the recoveries
  it leads to (a failed load's `failure_recovery`, a reconnect, a fresh attempt) carry it on, and so does one that
  starts while its new stream is still on its way (a subtitle added meanwhile), so it keeps the name of the change the
  viewer asked for. A request made once a stream plays names its own. It ends at the new
  stream's first frame, when a recovery ends with no load on its way (a refusal leaves the old stream playing), on a
  failure and at the stop, so no path leaves it on. A reconnect on its own is no plan change. The player shows it
  (`PlayerViewModel.IsChangingPlan`, `IsWaiting`; docs/ui-design.md, Player).
- After a refused request mpv runs to the end of what it had read and pauses there before it reports the end.
  The engine keeps the last real position and pause from the refusal until the next load, so the end of that
  data is never taken for the viewer's position.
- **Media proxy** (`MediaProxy`, the Apple client's design): an `HttpListener` on 127.0.0.1 between mpv and the
  server. Each load opens a route (`IMediaRoute`); mpv gets `http://127.0.0.1:{port}/{secret}/{route}/{the plan URL's
  path and query, byte for byte}`. The route forwards GET and HEAD with the range headers to its own stream only (a
  progressive stream: its exact URL; HLS: the playlist and what lies beside it, no dot segments), adds the headers per
  request, renews a token within 60 s of its end, and on a 401 renews it once and repeats the request, so mpv never
  sees an expiry. Redirects are not followed, and cookies and the upstream's other headers stay behind. A server that
  sends nothing for 60 s, before its answer (a token refresh included) or within its body (`UpstreamWatch`), is cut
  off: the server closes a stream a paused player stopped reading after 180 s, and word of that close may never
  arrive. Only waits for the server are timed, never a write the player is not taking.
- **Resume** (`UpstreamResume`, as Silo's Apple app reconnects its reader at the byte it had reached): a body that
  breaks off, is cut or ends early goes on from its next byte in the same answer, so mpv never sees the break. A
  server, or a proxy or firewall in front of it, that drops long downloads now and then otherwise ended the stream:
  mpv's reader counts every retry since the last seek against `curl-max-retries`, ones that brought data too, so five
  breaks anywhere in an episode made it give up, play out its cache and buffer. Only an answer that can be asked again
  from any byte, and checked, is resumed: a 206 with its `Content-Range`, or a 200 with `Accept-Ranges: bytes`, each
  with a length, not encoded, and with a strong `ETag` or a `Last-Modified` (with neither, a changed file of the same
  size would pass); a remux is not. The resume asks `Range: bytes=<next>-` (to the answer's own end when it ended
  before the file's) with the headers of the moment, none of the player's conditions, and the first answer's strong
  `ETag`, or else its `Last-Modified`, as `If-Range`, each sent back as the server wrote it. Only a 206 whose range
  starts at that byte and ends within the answer, with the same total and the validators the first answer had, is
  spliced in, and only up to the last byte it names, whatever its body sends past it. A 200 (the file changed),
  another range, a 416, no login, a refusal its one renewal did not cure (the Apple app's reader gives those a short
  ladder; here the player's own request meets the refusal and the playback reloads), or any other answer (a 404, a
  501) gives up at once. A busy or failing server (429, 500, 502, 503, 504, 509), a lost connection, a cut, or no token
  for now is asked again: at once after a break, then 1, 2, 4 and 8 s apart, longer for a `Retry-After` up to 15 s.
  Each broken or busy answer is let go before the next wait. It gives up after 12 resumes in a row that brought no
  data (a resume whose connection delivers starts that count again, as the Apple app's does), once a break has gone
  90 s without data whatever each try waits (Silo's Android app retries a load within 90 s, and the stall watch gives a
  stream as long), or after more than 30 resumes within 60 s, which a server that cuts every few kilobytes would have
  go on unpaced. The route's closing ends a resume at once, its waits too, and so does a newer GET of the same URL on
  the route: mpv reads a direct play file through one request at a time and cannot say when it left one for a seek, so
  an abandoned resume would hold one of the route's 16 places up to its 90 s. HLS segments have URLs of their own and
  are read side by side, untouched by this. Resumes and give-ups are logged with the byte, never the URL or headers.
- What cannot be resumed aborts the response, so mpv sees a lost connection, not an end. Its curl reader then asks
  again from its offset where the stream can seek, at once; mpv counts those retries since the last seek, so Granary
  allows 20 (`curl-max-retries=20`, its own option): room for the proxy's give-ups across a long title, but not for
  long on a server that is gone. A remux ends and the playback reconnects. Cuts and bodies that broke off are logged
  without URL or headers.
  The secret keeps other programs on the machine out; the log redactor masks it. A new load, the stop and a failure
  close the route and its requests. Before the proxy, mpv kept the headers for the whole load and would have sent them
  wherever a redirect or a playlist pointed.
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
  bundled with Granary. Only the SDK's loader and projection come with Granary, through the Windows App SDK's WinUI
  package (THIRD_PARTY_NOTICES.md).
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
- **Messages.** In a fixed schema (`TrailerPageMessage`: ready, playing, an error code, close, or fullscreen; 128
  characters at most, no other member). From the page's own origin all of them; from the page's iframe, only while it is
  YouTube's embed, close and fullscreen alone (`TrailerWebPolicy.FromPage`, `FromEmbed`). Anything else is ignored.
- **Keys.** Once the viewer clicks into the video, the keyboard belongs to the browser, and Granary's Esc and Back never see
  it. So `TrailerPage.CloseKeysScript` is added to every document of the WebView
  (`AddScriptToExecuteOnDocumentCreatedAsync`). Esc, the Back key, Alt+Left and the mouse's back button post
  `{"kind":"close"}`; in full screen Esc only leaves full screen. F11 posts `{"kind":"fullscreen"}`, which toggles the
  window's full screen (Window modes); a held key's repeats post nothing, and whatever a frame posts, Granary takes at
  most one every 500 ms (`TrailerOverlayViewModel.FullScreenInterval`), as each toggle resizes the window. Checked with synthetic events in the page and in
  YouTube's frame; a frame can do no more with them than close the trailer or toggle full screen. The script runs beside
  the page's own and is not part of its policy hash.
- **States.** `TrailerOverlayViewModel`: Loading, Playing (the player answered), Unavailable with its reason (the player's
  errors 2, 5, 100, 101, 150, 153 and any other; no answer within 15 s of the page opening, or no page within 30 s of the
  start, both through `TimeProvider`; no runtime; the browser's, the page's or a frame's process gone), Closed. Closing
  closes the WebView2 once the WebView's own event that closed it has returned; that stops the video and ends its
  processes.
- **Full screen.** When the page asks for it (`ContainsFullScreenElementChanged`), the trailer tells `WindowModeService`
  (see Window modes): from a window the window goes full screen with it and back, in the app's full screen it stays as it
  is. The app under the trailer is hidden meanwhile. When the window leaves full screen another way (F11), the page is
  asked to leave its own.

## Theme music

A title page plays its local theme music while Settings → Title Pages → Theme music is on (docs/api-contract.md, 6.6.4 and
7.19). Behaviour follows the apps (Android's `ThemeMusicCoordinator` and `ThemeMusicPlayer`, Apple's `ThemeMusicCenter` and
`ThemeMusicController`), which follow the web's `useThemeMusic.ts` and `lib/themeMusic.ts`.

- **`ThemeMusic`** (Core, a singleton) decides when. A title page's view model shows it a token of its own with the page's
  themes (none while it loads) and hides it as the page leaves; it never holds the page. Nothing plays unless the acting
  profile's own settings say so and the server's capability says themes are available (read again once a minute old; when
  they are routed, with whether the server authorizes media origins, which decides the proxy nodes a theme may come from,
  `ThemeOrigins`). A page that leaves or loads fades its theme out and pauses it; two seconds later (`ReleaseGrace`) the theme is let go, unless a
  page shows the same theme again, as a season with its series' set does, which goes on where it was. While the player
  holds a title, over the window or as its bar, or a trailer is open, the theme stops at once and starts over after
  (`OtherMediaPlaying`, which MainWindow sets once the window's turn is done, so a player taking over from a trailer does not
  let it start in between; a title replacing the one that played counts from its Play, while the old player lets the
  picture go, until it opens or fails to, the player closes, or 3 s have passed, a `DispatcherQueueTimer` through
  `WeakTick`). A minimized window counts as a page that left (`IsMinimized`, from `AppWindow.Changed`). A
  profile, server or sign-out stops it at once and forgets the capability.
- **`ThemeMusicPlayer`** (Core) plays one theme: the set's first, faded in to 35 % in 12 steps over 300 ms once it is heard and
  out the same way, on `TimeProvider` timers whose ticks go through `IUiDispatcher`. An original loops in place; a conversion
  ends and plays again from a new grant. A grant or download lost on the way (network, 5xx, timeout) or audio that fails
  is tried once more with a new grant, audio that plays earning the try back; a refusal (`ThemeFetch.Refused`) is not.
- **`ThemeAudioDownloader`** (Core) fetches the granted theme whole, without credentials, cookies or redirects, on the
  `theme-audio` client and its own handler, which logs nothing: the URL carries its own token and stays in Core. The
  handler's `ConnectCallback` connects a proxy node only to a resolved address the node's rule takes (`ThemeNode`, set on the
  request by `PlaybackUrl.ResolveTheme`), so a name that resolves to this PC or an unvouched private address is refused
  where the address is known. A node goes on the `theme-audio-node` client, whose handler never takes the system's proxy
  (the proxy would resolve the name out of the rule's sight); the server's own origin goes on `theme-audio`, which keeps
  the proxy and connects as it resolves.
- **`WindowsThemeAudio`** (App) implements the port on `Windows.Media.Playback.MediaPlayer` from the bytes (a light player beside
  mpv, not a second mpv), with its command manager off, so it stays out of Windows' media overlay and the media keys stay
  the player's. Its events come on Windows' threads; Core takes them to the UI thread.

## Window modes

The main window is a window (maximized or not), full screen, or the player's mini view (CompactOverlay), and that is one
state for the whole app: the title bar's button and F11, the player (F, its buttons, P, Esc) and a trailer's full
screen all go through `WindowModeService` (App, a singleton that MainWindow attaches to itself).

- **Decisions** are Core's `WindowModes`, without WinUI and unit-tested: which mode comes next. Leaving the mini view
  returns to where it was opened from; leaving the player or minimizing it ends only the mini view; a trailer's full
  screen takes the window only from a window and gives it back, and the viewer's own full screen outlasts it; leaving
  full screen ends a trailer's.
- **The window**: the service applies a change through the AppWindow presenters. It keeps the window as it is while it is
  a window, on every move and resize, and once more as it leaves (`WindowedPlacement`: the presenter, so a maximized
  window comes back maximized, and Core's `WindowPlaceKeeper`: the place and size it last had restored; while maximized,
  the place Windows restores it to, from `GetWindowPlacement`'s `rcNormalPosition` turned from workspace into screen
  coordinates by the work area's offset on the display that rect lies on (`DisplayArea.GetFromRect`, as Chromium uses
  `MonitorFromRect`; `WindowBounds.FromWorkspace`), so it follows a maximized window to another display
  (Win+Shift+Arrow). It is trusted only while its title bar lies on the display the maximized window is on
  (`WindowPlaceKeeper.KeepMaximized`); otherwise the last place stays, moved onto that display if it is not there, so a
  convention read the wrong way never sends the window elsewhere. Nothing of a minimized one, nor bounds that are no
  real place, empty or at -32000, as a window being destroyed may give); full screen and the mini view go to each other
  without it,
  so either returns to the window as it was before both. Coming back, a restored window keeps
  its place and size as long as its title bar (32 view px high, at least 120 wide) shows on some display's work area
  (`WindowBounds.OnDisplays` over `DisplayArea.FindAll`): across two displays, on one left of or above the primary, or
  flush with an edge. Only one whose title bar shows on none, its display unplugged or moved meanwhile, is moved onto
  the nearest display and made smaller only if it does not fit. A presenter the window took by itself is followed, and
  so is the window's real presenter when setting one fails (logged). Each start begins as a window, or in full screen
  when Granary closed in it (below).
- **Across starts**: the service saves that same windowed place to `window.json` in the data directory (Core's
  `WindowPlaceFile`): the restored bounds in pixels, the scale of the display they lie on (its effective DPI through
  `GetDpiForMonitor`), whether it was maximized, and whether it is full screen (`WindowModes.ReopensInFullScreen`: the
  viewer's own full screen, not a trailer's own, not the mini view, and not while minimized, `IsIconic`; read leniently,
  so a missing or odd value is a window and the place still holds):
  `{ "x": 312, "y": 140, "width": 1440, "height": 900, "scale": 1.5, "maximized": false, "full_screen": false }`. It saves once the window
  settles, 1 s after the last change it kept or the last change of mode, one the window made by itself included (a
  `DispatcherQueueTimer` through `WeakTick`), off the UI thread, so a crash,
  a kill or Windows shutting down keeps it too: asking for it swaps the place to write in (`Interlocked`) and never waits
  for a write in flight, which alone holds the file's lock. And as the window closes (`Window.Closed`, so also full
  screen's Close and a restart into an update), on the UI thread, after a write in flight (waited for at most 2 s,
  `AppUpdatesViewModel.SaveWaitAtExit`, as closing waits for the other saves; a write held longer, on a slow or network
  data folder, is logged and the close's save given up) and in place of one not yet written; that save is the last word: the service keeps nothing the closing window still reports (the presenter it
  leaves full screen for) and starts no settle save, and the file writes none that comes after it. A place the file
  holds already is not written again. Temporary files a Granary killed mid-save left (`window.json.<guid>.tmp`, older
  than a minute) are removed as the file is read at start. Closed in full screen or the mini view, that is the window it would have gone
  back to; closed minimized, the last place it had. MainWindow's constructor puts the window there while it is still
  hidden (`WindowModeService.OpenAtLastPlace`, Core's `WindowPlace.OpenOn`): on a display of another scale it keeps its
  size in view pixels, as Windows resizes a window moved between displays, and is fitted onto that display if it then
  does not fit; otherwise the rule above. Maximizing shows a window, so a maximized one is maximized in
  `WindowModeService.Show`, which App calls in place of `Activate`, right before the activation: the first frame is
  already maximized, and un-maximizing goes back to its bounds. Should the launcher's show command (a shortcut's) take
  the place of that first show, a window shown restored is maximized after the activation, and one shown minimized gets
  `WPF_RESTORETOMAXIMIZED`, so it comes back maximized from the taskbar and counts as maximized meanwhile (both logged
  at Debug; with `Start-Process -WindowStyle Normal` or `Minimized` the first show stayed maximized). One closed in full
  screen goes full screen in `Show` too, before the activation, so its first frame is full screen; it is placed at its
  windowed place first, which leaving full screen goes back to, and one also closed maximized is not maximized then
  (that would show it as a window) but on its way out of full screen (`WindowedPlacement.MaximizeOnRestore`), counting as
  maximized meanwhile; a window that is a restored window again before (kept so, or taken out of full screen by Windows
  itself) is not maximized later. Should a launcher's show command take full screen from it, a window shown as a window goes full
  screen after the activation, and one shown minimized as it comes back from the taskbar (logged at Debug; with
  `Start-Process -WindowStyle Minimized` it opened in full screen, not minimized). Files
  are written as other small files are (a temporary file, then `File.Move`, tried again for a moment while something has
  the file open); a write that fails is logged. No file, or one that cannot be read (logged), and Windows places the
  window at 1440 × 900 px. Unreadable counts a file over 64 KiB, which is not read at all, and a place out of range:
  more than 65,536 px out or across, or a scale outside 0.5 to 5 (Windows' 100 % to 500 %, with room). The same check
  keeps such a place from being written.
- **The player's full screen** across titles (Core's `PlayerFullScreen`, unit-tested): as the player is left
  (`PlayerPage.Leave`, after the mini view ended, and `PlayerPage.LeaveWithWindow` as the window closes, which leaves the
  mini view as it is) it keeps whether the window is in the viewer's full screen (`WindowModes.IsViewersFullScreen`: the
  mini view opened from full screen counts, a trailer's own full screen does not), in `playback-device.json`
  (`player_full_screen`, `IPlaybackDevicePreferences.PlayerFullScreen`, written only when it changes), and
  `PlayerPage.Start`, which only a title opened from the app reaches (`MainWindow.Open`: not the bar brought back, the
  mini view left or the next episode), asks `WindowModeService` for full screen when it is set. An Esc that took the full
  player out of full screen counts as full screen for a leave within `PlayerFullScreen.EscapeWindow` (5 s, on the
  injected `TimeProvider`); any other key or click in the player, a window change or minimizing forgets it. The Esc is
  marked after `SetFullScreen(false)`, whose `Changed` (raised within the call) forgets it. Leaving changes nothing about
  the window.
- **Changed** tells MainWindow (its title bar buttons), the player (its `IsFullScreen` and `IsMiniView`) and the trailer.
- **Drag area**: the title bar's own buttons lie outside `SetTitleBar`'s element, so they take clicks. In full screen the
  window has no drag area at all (`InputNonClientPointerSource`'s caption region cleared); WinUI does not set it again
  while its element keeps its size, so MainWindow sets it once from the element on the way back. Checked by hit-testing
  the strip with `WM_NCHITTEST`.
- **Close** in full screen posts `SC_CLOSE` to the window, the command Windows' Close sends, so `AppWindow.Closing` and
  the player's stop run as on any close.
- **Logs.** The outcome only ("The trailer plays", "can't play in Granary: EmbedRefused"); a blocked navigation is logged
  without its address, its host only at Debug. A YouTube address carries the video's key and stays out of the log.

## Taskbar preview

The player's Previous episode, Play/Pause and Next episode under the window's taskbar preview (docs/ui-design.md, Player)
go through the port `ITaskbarToolbar` (Core): show or change the three buttons' state (`TaskbarToolbarState`: each
hidden, disabled or enabled, and Play/Pause's paused), hide them all, and a pressed event naming the button.

- **`PlayerTaskbarToolbar`** (Core, beside `PlayerSystemMedia` and made and disposed with it by the player page) follows
  the player: shown from a title's load until the player is left, Play or Pause by `IsPaused`. Each button is enabled by
  the rule Windows' media controls use, which `PlayerSystemMedia` shares: `PlayPauseWorks`, and `PreviousWorks` and
  `NextWorks` (its `CanChangeEpisode` and the player's `HasPreviousEpisode` or `HasNextEpisode`). Previous and Next
  show only while `MediaInfo.Kind` is an episode. Until a new title's details have come (`MediaInfo` is `None`) the
  kind it remembers holds while the phase is `Preparing`, so they grey, not vanish, while the next episode loads; a
  title that failed before its details came has none, and leaving the player forgets the kind. It shows only a change
  of that, never a position tick. A press is posted to the UI thread, so it runs after the window's message has
  returned, and runs the player's `PlayPreviousEpisodeCommand`, `TogglePauseCommand` or `PlayNextEpisodeCommand` if the
  rule still allows it. `PlayerViewModel` clears `HasLeft` on a new start only after the title before is cleared
  (`Phase`, `MediaInfo`), so nothing that follows the player shows that title again for a moment.
- **`TaskbarToolbarSender`** (Core) hands each state to the App's Explorer call and keeps it handed on. A failed send
  is sent again after 2 s, also when nothing changed (`PlayerTaskbarToolbar` sends only changes). It gives up only on
  three failures in a row spanning 10 s at least, and then hides the buttons as far as Explorer still takes it. Given
  up, a change at least a minute after the last failure tries again, and so does a new taskbar button. The clock is a
  `TimeProvider`, the retries run on the UI thread through `IUiDispatcher`.
- **`WindowsTaskbarToolbar`** (App) is the main window's thumbnail toolbar (`ITaskbarList3` through its vtable,
  `Interop/TaskbarList.cs`): ids 1, 2 and 3 for Previous, Play/Pause and Next, in that order. The window's messages come
  through `Interop/WindowMessageHook.cs`, the window's single comctl32 subclass, made with the window, which also takes a
  remote's WM_APPCOMMAND: Explorer's `TaskbarButtonCreated` (before it, `ITaskbarList3` is not called; after it, all
  three buttons are added in one `ThumbBarAddButtons`, again when Explorer restarts and makes a new taskbar button), a
  button's `WM_COMMAND` with `THBN_CLICKED` in the high word of wParam and its id in the low word, and `WM_DPICHANGED`,
  which remakes the icons. A toolbar's buttons can be neither added later nor removed, so `ThumbBarUpdateButtons` shows
  and hides them (`THBF_HIDDEN`) and greys them (`THBF_DISABLED`). The icons (`Interop/TaskbarIcons.cs`: ⏮, ▶, ⏸, ⏭)
  are drawn at `SM_CXSMICON` for the window's DPI into premultiplied BGRA and made with `CreateIconIndirect`; the taskbar
  copies them, and Granary destroys its own when they are replaced and when the window closes, with the COM object. A
  failed call drops the COM object, made again for the next try, and is the sender's to try again or give up on.

## Watch Party

`WatchPartySession` (Core) is the room this profile is in, for the whole app: the hub, the room page and the player's
panel follow it. It joins or opens the room over HTTP and keeps the room socket (`WatchPartyConnection`) while it is in it.

- **The end.** `End` says why the room is over for this device, and stays until the room is left or another entered.
  The room's own close (`room_closed` with its reason: `host_left` when the host ended it or stayed away two minutes),
  an ended snapshot (`ended`), a refused socket ticket (`ended` on 409, `not_found` on 404, `forbidden` otherwise) and
  another device taking this profile's place (`connection_replaced`, `Replaced`) all end it. All but `forbidden` and a
  takeover say the room itself is over: it is marked ended among the recent rooms, and a title noted after that does
  not bring it back. A takeover can be claimed back (Rejoin).
- **The host's End.** `DELETE …/rooms/{id}`; the socket closes and `End` is `host_left`, as the others hear it. An
  answer that the room is over already (409 `conflict`, 404 `not_found`) ends it too, as `ended` or `not_found`, as
  Android's `WatchTogetherRepository.closeRoom` takes the 409. The room's own close may arrive while the End is on its
  way: an end that says the room is over is kept, one that said less (a refused proof, a takeover) gives way. Ends on
  their way are kept by room and entry (the session's generation): a second End of the same entry waits for the first,
  an End of another room or of the same room entered again goes out on its own, and a failed End is not waited for.
  An End that goes through, or finds the room over, marks it ended for the server and profile that entered it. If this
  device is in that room by then, entered again meanwhile included, that engagement ends too; if it left, or is in
  another room, nothing else changes.
- **To the hub.** A room that is over is not shown. The room page and the player each go on to the hub, unless it shows
  already, as a section (`ShowSection`), so Back does not return to the room; the player first leaves as on any exit,
  its position reported. The hub says why in the web's words (`WatchPartyText`) until it is dismissed, which leaves the
  room on this device. A room that only stopped playing is still on: the player closes, and the room page under it shows
  again.
- **From a title's page.** `StartPartyViewModel` (Core, one per detail hero) offers "Start a Watch Party" while the page
  has a title (`ItemDetailViewModel.WatchPartyRequest`), the server's capabilities say rooms with `staged_selection`, and
  the session is in no room. Its sheet creates the room through the session, so the session has the room's proof at
  once: it changes the pause policy where the switch says otherwise, stages the title or (in a vote) suggests it, and
  copies the invite before the room page opens, which says what happened on the way (`WatchPartyArrival`, the web's
  toasts). Each step is for the server and profile the sheet was opened for; a change of either ends it quietly, and a
  sheet put away or a page left mid-create still joins the room but neither copies the invite nor opens the room page.
  Its commands catch every failure, as an exception out of an `AsyncRelayCommand` ends the app: a failed create is said
  in the sheet, a failed step after it only logged once the room stands. The read of the server's rooms an attach
  starts is waited for by nobody, so it catches everything too, and `WatchPartyFeature` drops an answer read for the
  server and profile before.

## Discord status

An opt-in status in Discord while a title plays (Settings → Playback → This PC,
`IPlaybackDevicePreferences.DiscordStatus` in `playback-device.json`); docs/ui-design.md says what it shows.

- **`PlayerRichPresence`** (Core, beside `PlayerSystemMedia` and made and disposed with it by the player page) reads the
  player and the setting and shows the result on the port `IRichPresence`. Like `PlayerSystemMedia`, it sends only a
  change of what shows: Discord runs the timeline on by itself, so a position tick sends nothing, a jump of more than 3
  seconds does. Discord takes about 5 updates in 20 seconds, so one goes out at most every 4 seconds; a change that has
  to wait goes out on a timer as the player is by then. Clearing goes at once. While a title loads, `PlayerViewModel`
  still holds the position, length and pause of the title before, and it sets `Phase` to Playing before it reads the new
  stream's state. Its seek bar (`CanSeek`) goes off as a load starts (`PlayAsync`) and comes on from a started stream of
  that load, after the position, length and pause; so from a load on, a title shows once `CanSeek` was off and is on
  again while it plays, whichever of the player's changes comes first. A file played directly
  (`PlaybackMediaKind.Direct`) never shows, and a lookup's answer is taken only while its title still plays.
- **`TmdbTitleLookup`** (Core, a singleton) finds the poster and the TMDB page through the server, so Granary needs no
  TMDB key of its own: the catalog item's `tmdb_id` (the movie's, an episode's series'), then the requests detail's
  `poster_path` (docs/api-contract.md, 7.6 and 7.15). It runs only while the setting is on, once per title, server and
  profile (the session's, which the requests go out as: a child's profile may be refused what another sees), and keeps
  the answer for the run, none included; an answer read while the session went to another profile or server is dropped,
  and a server that could not be reached or was busy (429, 5xx) is asked again for the next title. Only TMDB's own
  addresses (`TmdbImages.Poster`, `www.themoviedb.org`) reach Discord: an address of the user's server would show it to
  everyone who looks at the profile.
- **`DiscordStatus`** (App) implements the port with the DiscordRichPresence library over Discord's local pipe, as
  Granary's Discord application (`DiscordStatus.ApplicationId`, whose Rich Presence assets hold the `granary` picture).
  It connects with the first status to show, sends only while Discord is connected, and the latest status again when
  Discord connects or restarts. Without Discord the library keeps trying in the background, which the log says once; a
  clear while Discord is not there stops it. It is disposed when the window closes. The library raises its events on its
  own thread, under the same lock as the UI thread's calls; a client is let go under the lock and disposed outside it.

## Windows App SDK

The App is unpackaged and self-contained (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`): the Windows
App SDK's runtime files ship beside `Granary.exe`, and the app needs neither an installed Windows App Runtime nor the
bootstrapper. A self-contained build ships every part it references, so Granary references the parts it uses instead
of the `Microsoft.WindowsAppSDK` metapackage:

| Package | What Granary uses it for |
|---|---|
| `Microsoft.WindowsAppSDK.WinUI` | WinUI 3, its XAML compiler, and through it `Microsoft.Web.WebView2` for the trailers |
| `Microsoft.WindowsAppSDK.InteractiveExperiences` | `Microsoft.UI.Composition`, `.Input`, `.Windowing` and `.Dispatching`, which WinUI is built on |
| `Microsoft.WindowsAppSDK.Foundation` | MRT Core for the app's resources (`Granary.pri`), the file pickers (`Microsoft.Windows.Storage.Pickers`), and the start-up that lets the app find the parts' classes without a package (undocked reg-free WinRT) |
| `Microsoft.WindowsAppSDK.Base` | The self-contained build: it copies each referenced part's runtime files and lists their classes in the app manifest. Nothing of it ships |

Left out, and so not shipped: AI, ML (DirectML, ONNX Runtime), DWriteCore (WinUI draws text with the system's
DirectWrite), Widgets, Search, and `Microsoft.WindowsAppSDK.Runtime`, whose framework packages only a
framework-dependent app installs. That keeps 67 files and about 60 MB out of the build. The versions in
`Directory.Packages.props` are the set one metapackage release pins in its `.nuspec`, and they move together (Dependabot
proposes them in one pull request). Using another part, for example the AI APIs, means adding its package; until then
its classes are missing at runtime ("class not registered"). Licenses and published source: THIRD_PARTY_NOTICES.md.

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
| The viewer's mpv.conf read by mpv itself, Granary's options in the `pseudo-gui` profile | mpv's syntax and `~~/` need mpv's own loader, which applies the file inside `mpv_initialize`; that profile is the one order libmpv offers that puts Granary's options after the file and before mpv acts on them. |
| Header-authenticated media (`header_authenticated_media_v1`) | No tokens in URLs or logs; same mode as the Apple client. |
| DPAPI file for tokens | The Credential Locker roams with the Microsoft account and is capped per app. |
| Session state derived from registry and credentials | One source of truth; handler-initiated sign-outs and user actions take the same path to the UI. |
| xUnit v3 with built-in asserts, no mocking library | FluentAssertions 8 is commercially licensed; fakes are small hand-written classes. |
| Trailers in YouTube's embedded player in WebView2 | YouTube's terms allow its player only; mpv or yt-dlp would break them. The web plays them the same way. |
| Central package management, warnings as errors | One place for versions; analyzers (including AOT/trim and platform checks) are enforced. |
| Windows App SDK parts instead of the metapackage | A self-contained app ships every part it references; the AI, ML, DWriteCore, Widgets and Search parts are about 60 MB Granary never calls. |

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
