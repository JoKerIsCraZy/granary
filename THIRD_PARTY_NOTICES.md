# Third-party notices

Granary is licensed under `AGPL-3.0-or-later`. This file lists the third-party components it uses
or distributes, their licenses, and where their source code is available. It is updated whenever a
dependency changes.

Status: Phase 1 spike. The libmpv build for releases is chosen in Phase 2; this entry then gets the
exact release artifact and its checksum.

## libmpv (bundled native library)

| | |
|---|---|
| Component | libmpv (`libmpv-2.dll`), part of mpv |
| Version used | `v0.41.0-dev-ga39482e7a` (mpv commit `a39482e7a`), FFmpeg `git-2026-10-24-1d83e71` |
| Origin | Official mpv CI build, release `git-release` of `mpv-player/mpv`, asset `libmpv-v0.41.0-dev-ga39482e7a-40099161509-x86_64-w64-mingw32-gpl.zip` (SHA-256 `7876f7af31738f570e41ee1406ec39ddc8ea1ca061f34158340eac9acb5449af`) |
| Build | `ci/build-mingw64-full.sh` in the BtbN `win64-gpl` FFmpeg build container; `-Dgpl=true -Dlibmpv=true`, statically linked dependencies |
| License | mpv: GPL-2.0-or-later (`-Dgpl=true`). FFmpeg in the BtbN GPL variant is built with `--enable-gpl --enable-version3`, so it is GPL-3.0-or-later. The combined library is distributed under **GPL-3.0-or-later**. |
| Statically included | FFmpeg with the libraries of the BtbN `win64-gpl` recipe, plus the mpv dependencies enabled in the build configuration (`mpv-configuration`): LuaJIT, MuJS, lcms2, libbluray, libcurl, shaderc, SPIRV-Cross, Vulkan, zimg, Rubber Band, libdvdnav, VapourSynth, AMF, libsixel, libass, libplacebo. The complete per-library license list is generated from the BtbN recipe in Phase 2. |
| Source | mpv: <https://github.com/mpv-player/mpv> (commit `a39482e7a`). FFmpeg: <https://git.ffmpeg.org/ffmpeg.git> (commit `1d83e71`). Build recipe and dependency versions: <https://github.com/BtbN/FFmpeg-Builds> |

Compatibility: GPL-3.0 section 13 and AGPL-3.0 section 13 explicitly allow combining GPLv3 and
AGPLv3 code, so the GPL-3.0-or-later library can be distributed with the AGPL-3.0-or-later app.
Distributing builds requires offering the corresponding source of the bundled library, which the
links above provide; a release must pin and mirror the exact sources it ships.

An LGPL variant of the same build exists (`…-lgpl.zip`, `-Dgpl=false`). It is not needed for
licensing reasons because Granary is AGPL anyway; Phase 2 compares size and features.

## .NET packages

| Package | Version | License | Source |
|---|---|---|---|
| Microsoft.WindowsAppSDK | 2.5.1 | MIT | <https://github.com/microsoft/WindowsAppSDK> |
| Vortice.Direct3D11, Vortice.DXGI (spike only) | 3.8.3 | MIT | <https://github.com/amerkoleci/Vortice.Windows> |

## Silo

Granary talks to Silo Server over its native API. It contains no Silo code. "Silo" is a trademark
of Silo Media L.L.C.; Granary is an independent client and not an official Silo product.
