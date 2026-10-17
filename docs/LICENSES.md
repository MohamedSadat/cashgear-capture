# Licences of bundled components

`THIRD-PARTY-NOTICES.txt` (shipped next to the app) lists every third-party component and its licence. This page explains the one that needs care: FFmpeg.

## FFmpeg (screen recording)

**Decision (Phase 10):** recording runs FFmpeg inside the app through FFmpeg.AutoGen, linked dynamically to an **LGPL** build. Only an LGPL build keeps the app free to be closed source and sold (Phase 19). A GPL build (with x264) would make the app itself GPL. Before Phase 10 the app ran a GPL `ffmpeg.exe` as a separate program, which doesn't link to it; that is no longer shipped.

| | |
|---|---|
| Build | [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), `win64-lgpl-shared`, FFmpeg `n9.0.2-17-g2a571b6068` (release/9.0 branch), month-end autobuild tag `autobuild-2026-09-30-13-08`, asset `ffmpeg-n9.0.2-17-g2a571b6068-win64-lgpl-shared-9.0.zip` (SHA-256 `7157177b8a6cb2174c1650ba8c71b363f2c78cba5330f88c4c02cf5b2b880646`). `tools\get-ffmpeg.ps1` downloads and checks it. BtbN keeps month-end builds for two years. |
| Licence | GNU Lesser General Public License **version 3** (the build uses `--enable-version3`), shipped as `ffmpeg\LICENSE.txt`. LGPL v3 adds permissions to the GNU GPL v3, whose text ships as `ffmpeg\COPYING.GPLv3` (from FFmpeg's source at `n9.0.2`). |
| What ships | `ffmpeg\avutil-61.dll`, `swresample-7.dll`, `swscale-10.dll`, `avcodec-63.dll`, `avformat-63.dll`, `avfilter-12.dll` (since Phase 11, for evening out loudness with `loudnorm`; the editor and GIF export will use it too), the two licence texts. Not shipped: `avdevice`, `ffmpeg.exe`, `ffprobe.exe` (only the tests use them). |
| Source | FFmpeg: <https://github.com/FFmpeg/FFmpeg/tree/2a571b6068> (tag `n9.0.2` plus 17 release-branch commits). Build scripts and the versions of every library compiled in: <https://github.com/BtbN/FFmpeg-Builds/tree/autobuild-2026-09-30-13-08>. For a commercial release, also host a copy of both next to the download (LGPL v3 section 4, GPL v3 section 6), rather than relying on GitHub alone. |
| Replacing it | The libraries are separate DLLs loaded from `ffmpeg\` at run time, so a user can swap in their own build of the same major versions (`avcodec-63` and so on). The app checks the versions and explains a mismatch instead of crashing. |
| Bindings | FFmpeg.AutoGen.Abstractions and FFmpeg.AutoGen.Bindings.DynamicallyLoaded 9.0.1.1, MIT. |

### Libraries compiled into the FFmpeg DLLs

The LGPL build includes no GPL-only parts (no x264, x265, xvid or vidstab). Among the libraries in it, under their own permissive or LGPL licences, are:
- OpenH264 (BSD-2-Clause);
- libvpx (BSD-3-Clause);
- dav1d (BSD-2-Clause), libaom (BSD-2-Clause) and SVT-AV1 (BSD-3-Clause + AOM patent licence);
- Opus (BSD-3-Clause) and LAME (LGPL);
- libxml2 (MIT), zlib (zlib), FreeType (FTL), HarfBuzz (MIT) and fribidi (LGPL);
- libass (ISC), libwebp (BSD-3-Clause) and OpenJPEG (BSD-2-Clause).

The exact list is in the build's configure line (`ffmpeg -version`) and in the build scripts linked above.

### Codec patents

Licences cover copyright, not patents.
- **H.264 and HEVC** are patent-encumbered in some countries.
- **Media Foundation.** The app prefers encoders whose licensing is someone else's: the GPU vendor's hardware encoder (NVENC, Quick Sync, AMF), or Windows' own Media Foundation encoders, whose licensing comes with Windows.
- **OpenH264.** It is the last fallback (Windows N editions without the Media Feature Pack). Cisco's patent licence for OpenH264 only covers Cisco's own binaries, and this one is compiled from source by the FFmpeg build.
- **AAC.** Sound uses FFmpeg's own AAC encoder.

Get advice before selling the app in markets where this matters (Phase 19).
