# Licences of bundled components

`THIRD-PARTY-NOTICES.txt` (shipped next to the app) lists every third-party component and its licence. This page explains the one that needs care: FFmpeg.

## FFmpeg (screen recording)

**Decision (Phase 10):** recording runs FFmpeg inside the app through FFmpeg.AutoGen, linked dynamically to an **LGPL** build. Only an LGPL build keeps the app free to be closed source and sold (Phase 19). A GPL build (with x264) would make the app itself GPL. Before Phase 10 the app ran a GPL `ffmpeg.exe` as a separate program, which doesn't link to it; that is no longer shipped.

| | |
|---|---|
| Build | [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), `win64-lgpl-shared`, FFmpeg `n9.0.2-17-g2a571b6068` (release/9.0 branch), month-end autobuild tag `autobuild-2026-09-30-13-08`, asset `ffmpeg-n9.0.2-17-g2a571b6068-win64-lgpl-shared-9.0.zip` (SHA-256 `7157177b8a6cb2174c1650ba8c71b363f2c78cba5330f88c4c02cf5b2b880646`). `tools\get-ffmpeg.ps1` downloads and checks it. BtbN keeps month-end builds for two years. |
| Licence | GNU Lesser General Public License **version 3** (the build uses `--enable-version3`), shipped as `ffmpeg\LICENSE.txt`. LGPL v3 adds permissions to the GNU GPL v3, whose text ships as `ffmpeg\COPYING.GPLv3` (from FFmpeg's source at `n9.0.2`). |
| What ships | `ffmpeg\avutil-61.dll`, `swresample-7.dll`, `swscale-10.dll`, `avcodec-63.dll`, `avformat-63.dll`, `avfilter-12.dll` (since Phase 11, for evening out loudness with `loudnorm`; the video editor's speed changes use `atempo`, and GIF export `palettegen` and `paletteuse` with avcodec's GIF encoder: no gifski, which is AGPL-3.0), the two licence texts. Not shipped: `avdevice`, `ffmpeg.exe`, `ffprobe.exe` (only the tests use them). |
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

## RNNoise (microphone noise suppression)

**Decision (Phase 11):** RNNoise has no official Windows binary, so `tools\get-rnnoise.ps1` builds `rnnoise.dll` from the release source with Visual Studio's C++ compiler. A binary from a third party would have no provenance we can check.

| | |
|---|---|
| Source | [xiph/rnnoise](https://github.com/xiph/rnnoise) release v0.2 (2024-04-15), `rnnoise-0.2.tar.gz`, SHA-256 `90fce4b00b9ff24c08dbfe31b82ffd43bae383d85c5535676d28b0a2b11c0d37`, checked by the script. The tarball includes the trained model (`src/rnnoise_data.c`), trained on publicly available data according to its release notes. |
| Licence | BSD 3-Clause (`COPYING` from the tarball), shipped as `rnnoise\COPYING`. It allows closed-source and commercial use; the notice must ship with the binary. |
| Build | x64, static C runtime (`/MT`, no Visual C++ redistributable needed), `/O2`; SSE2 everywhere, with SSE4.1 and AVX2 (+FMA) code chosen at run time by RNNoise's own CPU detection. |
| What ships | `rnnoise\rnnoise.dll` and `rnnoise\COPYING`. Without them the app works and Settings doesn't offer noise suppression. |
| Bindings | `CashGear.Capture.Interop.Native.Rnnoise` (`LibraryImport`), loaded only from `rnnoise\` next to the app. |

## Whisper (speech to text for captions)

Captions are made from speech with OpenAI's Whisper models run by [whisper.cpp](https://github.com/ggml-org/whisper.cpp) through [Whisper.net](https://github.com/sandrohanea/whisper.net).

| | |
|---|---|
| Packages | Whisper.net, Whisper.net.Runtime and Whisper.net.Runtime.Vulkan **1.9.1** (NuGet, 2026-06-01), MIT. The runtimes are whisper.cpp and ggml compiled by Whisper.net's maintainers, MIT. |
| What ships | `runtimes\win-x64\` (`whisper.dll`, `ggml-whisper.dll`, `ggml-base-whisper.dll`, `ggml-cpu-whisper.dll`: the processor) and `runtimes\vulkan\win-x64\` (the same with `ggml-vulkan-whisper.dll`, about 58 MB: the graphics card). The Vulkan runtime needs the graphics driver's own Vulkan loader (`vulkan-1.dll`), which isn't shipped; without it the processor's runtime is used. No CUDA runtime is shipped (the user's choice: Vulkan works with NVIDIA, AMD and Intel cards and needs no extra download). |
| Models | Not shipped: the one the user picks is downloaded on first use from <https://huggingface.co/ggerganov/whisper.cpp> (whisper.cpp's quantized ggml conversions of OpenAI's Whisper weights, MIT, <https://github.com/openai/whisper>) and checked against its SHA-256: `ggml-base-q5_1.bin` (57 MB), `ggml-small-q5_1.bin` (181 MB), `ggml-medium-q5_0.bin` (514 MB). `tools\get-whisper-models.ps1` downloads the same files for the tests. |
