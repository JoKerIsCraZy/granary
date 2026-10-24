# Phase 1 spike: libmpv in WinUI 3

Isolated experiment, not production code. It answers one question: how should Granary put
libmpv video into a WinUI 3 window with XAML controls on top?

**Result: variant C, mpv's d3d11 "composition" mode into a `SwapChainPanel`.** Variant A (render
API) is the fallback; variant B (child HWND) is ruled out.

## Variants

| | A: render API (D3D11) | B: child HWND (`--wid`) | C: d3d11 composition mode |
|---|---|---|---|
| Who owns the swap chain | the app (on a `SwapChainPanel`) | mpv (own window) | mpv; the app sets it on a `SwapChainPanel` |
| mpv requirement | master only (`render_d3d11.h`, April 2026) | any | v0.41.0+ (resize support since Sept 2025) |
| XAML controls over the video | yes | **no** (airspace) | yes |
| App code for presentation | device, swap chain, render thread, frame pacing | window placement | size, DPI transform, color target |
| Hardware decoding | d3d11va | d3d11va | d3d11va |

## Test results (2026-10-24)

Machine: Windows 11 Pro 26300, NVIDIA GeForce RTX 4080, two SDR monitors (3840×1600, 1920×1080;
Windows HDR off). libmpv `v0.41.0-dev-ga39482e7a` (official mpv CI build, GPL variant),
Windows App SDK 2.5.1, .NET 10.

| Test | A: render API | B: child HWND | C: composition |
|---|---|---|---|
| Local file, 1080p H.264 | plays, d3d11va | plays, d3d11va | plays, d3d11va |
| Frame drops at start | 11–12 | 0 | 0 |
| Resize (3 sizes) | 0 drops | 0 drops | 0 drops |
| Fullscreen 3840×1600 | 0 drops | 0 drops | 0 drops |
| XAML overlay visible | yes | **no**; with the child below XAML the video disappears | yes |
| 4K HEVC 10-bit HDR10 → SDR tone mapping | d3d11va, 0 drops after start | not tested | d3d11va, 0 drops, target BT.709/gamma 2.2 |
| PQ output (`--hdr-output`) | not tested | not tested | target PQ/BT.2020 set; needs an HDR display to judge |
| HTTP with `Authorization` header | every request authorized | – | every range request authorized |
| HLS with header | – | – | playlist and every segment authorized |
| `sub-add` external WebVTT over HTTP | – | – | header sent, track selected, cue shown |
| Teardown | "video output initialization failed" at close (order issue) | clean | clean |

Network behaviour (mode-independent, measured with `tools/authserver.py`):

- mpv's HTTP stream is libcurl (`-Dlibcurl=enabled`); `http-header-fields` applies to the media
  request, every HLS playlist/segment request and `sub-add` URLs.
- **Changing `http-header-fields` at runtime does not affect an open stream.** After the old token
  was revoked, the next range request (seek) still sent it and got `401`.
- **mpv treats that mid-stream `401` as end of file** (`eof-reached=yes`); the cause is only in the
  log (`curl: HTTP error 401`). A `401` at open ends the file with `error=loading failed`.
- Reloading with the new header at the current position (`loadfile <url> replace -1 start=<pos>`)
  resumed playback in about 100 ms.

Track identity: mpv `track-list/N/ff-index` equals the FFmpeg stream index (the server's
`embedded.stream_index`); `src-id` is the Matroska track number (`container_track_id`). Container
chapters are read by mpv.

## Why C

- Works with the stable mpv line and needs the least app code; mpv keeps its own frame timing,
  which was the only variant with no startup drops besides B.
- XAML controls draw over the video, which B cannot do.
- A gives the app more control (shared device, own presentation) but needs mpv master and correct
  frame pacing; keep it as a fallback if composition mode shows problems.

Consequences for Phase 2/3:

- Composition mode cannot see the monitor: the app passes the swap-chain size
  (`d3d11-composition-size`), applies the inverse DPI scale (`IDXGISwapChain2::SetMatrixTransform`),
  sets the HDR target (`target-trc=pq`, `target-prim=bt.2020`, `d3d11-output-csp=pq`,
  `d3d11-output-format=rgb10_a2`) when the window's monitor has HDR on, and should set
  `display-fps-override` from the monitor refresh rate (`display-fps` is unavailable).
- Detect `401` from the mpv log or a failed `end-file`, then refresh the token and reload at the
  position; never treat that EOF as "watched". A loopback proxy that injects the current header per
  request stays an option if reloads prove disruptive.
- The libmpv build ships its own Windows SMTC integration (`-Dwin32-smtc`); Granary drives SMTC itself,
  so it must turn mpv's off.
- Not tested here: monitors with different DPI scaling, HDR passthrough on an HDR display, real HDR
  and Dolby Vision content.

## Running it

Native files are not committed. Put `libmpv-2.dll` into `native/libmpv/` (from the official mpv
`git-release`, asset `libmpv-v0.41.0-dev-*-x86_64-w64-mingw32-gpl.zip`), generate test media with
`tools/make-testmedia.sh <ffmpeg.exe> native/testmedia`, then:

```powershell
dotnet build spike/Granary.MpvSpike -c Debug -p:Platform=x64
Granary.MpvSpike.exe --mode=composition|render|hwnd --source=<file or URL> [--selftest] `
  ["--header=Authorization: Bearer <token>"] [--sub=<url>] [--hwnd-zorder=bottom] [--hdr-output] `
  [--rotate-token-file=<server tokens file>] [--small-cache] [--log=<path>] [--shots=<dir>]
```

`--selftest` plays, seeks, pauses, resizes, toggles fullscreen and writes metrics and screenshots.
For the network tests run `python -I tools/authserver.py <media dir> <tokens file> <log file> 8765`.
Keys: Space play/pause, F fullscreen, Esc leave fullscreen.
