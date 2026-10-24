# Third-party notices

Granary is licensed under `AGPL-3.0-or-later`. This file lists the third-party components it uses
or distributes, their licenses, and where their source code is available. It is updated whenever a
dependency changes.

## libmpv (bundled native library)

| | |
|---|---|
| Component | libmpv (`libmpv-2.dll`), part of mpv |
| Version | `v0.41.0-dev-ga39482e7a` (mpv commit `a39482e7a`), FFmpeg `git-2026-10-24-1d83e71` |
| Variant | **LGPL** (`-Dgpl=false`) |
| Origin | Official mpv CI build, release `git-release` of `mpv-player/mpv`, asset `libmpv-v0.41.0-dev-ga39482e7a-40099161509-x86_64-w64-mingw32-lgpl.zip`, SHA-256 `9a084eab0b0b04e82f5f4b3cdd314fcc91eddae4ca1756d3ce24aa3718e1fb72` (pinned in `native.lock.json`) |
| Build | `ci/build-mingw64-full.sh` in the BtbN `win64-lgpl` FFmpeg build container; `-Dlibmpv=true -Dgpl=false`, dependencies linked statically |
| License | mpv: LGPL-2.1-or-later (`-Dgpl=false`); FFmpeg and its libraries in the BtbN LGPL variant are LGPL or more permissive. The library ships with its `LICENSE.LGPL`. |
| Enabled mpv dependencies | From `mpv-configuration`: LuaJIT, MuJS, lcms2, libbluray, libcurl, shaderc, SPIRV-Cross, Vulkan, zimg, VapourSynth, AMF, libsixel, plus libass and libplacebo. The complete per-library list follows the BtbN `win64-lgpl` recipe and is generated before the first release. |
| Source | mpv: <https://github.com/mpv-player/mpv> (commit `a39482e7a`). FFmpeg: <https://git.ffmpeg.org/ffmpeg.git> (commit `1d83e71`). Build recipe: <https://github.com/BtbN/FFmpeg-Builds> |

Granary loads the library dynamically (`libmpv-2.dll` next to the executable), so users can replace it
as the LGPL requires. Distributing builds requires offering the corresponding source; a release must
pin and mirror the exact archive and sources it ships, because the upstream asset is replaced on every
mpv master update.

The Phase 1 spike also ran the GPL variant of the same build (GPL-3.0-or-later, compatible with
AGPL-3.0-or-later via GPLv3/AGPLv3 section 13). Granary ships the LGPL variant: it is 30 MB smaller
and passed the same tests.

## .NET packages

Licenses as declared in each package's `.nuspec`:

| Package | Version | License | Used by |
|---|---|---|---|
| Microsoft.WindowsAppSDK | 2.5.1 | Microsoft Software License Terms (`license.txt` in the package) | App; its runtime is redistributed because the app is self-contained |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | Core |
| QRCoder | 1.8.1 | MIT | App (QR code for device sign-in) |
| Microsoft.Extensions.DependencyInjection(.Abstractions), .Http, .Logging(.Abstractions) | 10.0.13 | MIT | Core, Player, App |
| System.Security.Cryptography.ProtectedData | 10.0.13 | MIT | Core |
| xunit.v3 | 4.0.2 | Apache-2.0 | Tests only |
| Microsoft.Testing.Extensions.CodeCoverage | 18.12.0 | Microsoft .NET Library License (`License.txt`) | Tests only, not distributed |
| Vortice.Direct3D11, Vortice.DXGI | 3.8.3 | MIT | Tests and spike only, not distributed |

Open point: the Windows App SDK runtime is proprietary and not a system library of Windows. Before
the first release, decide how the AGPL-3.0-or-later licensing handles linking with it (for example an
additional permission for the Windows App SDK, similar to the App Store permission in silo-apple).

## Plus Jakarta Sans (bundled font)

| | |
|---|---|
| Component | Plus Jakarta Sans, the typeface of Silo's web client |
| Version | 2.7.1, static TTF files: Regular, Italic, Medium, SemiBold, Bold, ExtraBold |
| Origin | Release archive `PlusJakartaSans-2.7.1.zip` of <https://github.com/tokotype/PlusJakartaSans>, SHA-256 `4bfc5cdf97d750423bb3d1d40ed8e529bc92288924d9c65e18ff486acefac66c` |
| License | SIL Open Font License 1.1; the license ships next to the fonts (`src/Granary.App/Assets/Fonts/OFL.txt`). The OFL allows bundling the fonts with software under any license. |

## Silo

Granary talks to Silo Server over its native API and contains no Silo code. "Silo" is a trademark of
Silo Media L.L.C.; Granary is an independent client and not an official Silo product.
