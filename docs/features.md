# moist — feature guide

[← back to the README](../README.md) · [Features](features.md) · [Command line](cli.md) · [FAQ](faq.md) · [Development](development.md)

Every feature, in depth. The [README](../README.md) is the tour; this is the manual.

## Video Encoding

The heart of the operation. No shortcuts, no compromises.

- **Video Codecs**: x264, x265, SVT-AV1, VP9, FFV1 — via FFmpeg or standalone CLI
- **Hardware Encoding**: NVENC, plus experimental Quick Sync, AMF, VideoToolbox and VA-API (H.264 / H.265 / AV1)
- **Bit Depth**: 8, 10 or 12-bit, or Auto to follow the source — 10-bit HDR masters stay 10-bit
- **Encoding Modes**: CRF, constant quality, single-pass bitrate, two-pass bitrate
- **Full Preset & Tune Control**: Every x264/x265/SVT-AV1 preset and tune option exposed
- **SVT-AV1 Numeric Preset**: Direct 0-13 preset control for fine-grained AV1 speed/quality tradeoff
- **Two-Pass Encoding**: Automatic two-pass handling for FFmpeg and standalone CLI encoders
- **Custom Parameters**: Pass arbitrary encoder arguments for those who know what they're doing

## Audio Encoding

Every knob, every dial. Professional audio encoding with nothing hidden.

- **Audio Codecs**: Opus, AAC, MP3 (LAME), FLAC, AC3 (Dolby Digital), E-AC3 (Dolby Digital Plus), Vorbis
- **Audio-Only Mode**: Encode audio directly without video — outputs to proper codec-specific extensions (.opus, .m4a, .mp3, .flac, .ac3, .eac3, .ogg)
- **Bitrate Modes**: VBR, CBR, Constrained VBR — per codec
- **Channel Control**: Keep original, mono, stereo, 5.1 surround, 7.1 surround
- **Sample Rate**: Full range from 8 kHz to 192 kHz, or keep original
- **Sample Format**: Auto, S16, S24, S32, Float
- **Audio Normalization**: EBU R128 (loudnorm), Peak (acompressor), RMS (dynaudnorm) with configurable LUFS target and true peak limit
- **Codec-Specific Options**:
  - Opus: Complexity (0-10)
  - AAC: Profile selection (LC, HE, HE-v2, LD, ELD)
  - MP3: VBR quality scale (0-9), joint stereo modes
  - FLAC: Compression level (0-12)
  - AC3: Dialogue normalization, surround mode
  - E-AC3: Dialogue normalization, mixing level
  - Vorbis: Quality scale (-1 to 10)
- **Audio Stream Selection**: Pick your stream, don't settle for whatever FFmpeg grabs first
- **Audio Passthrough**: Copy audio without re-encoding when the source is already right

## Filter Engine

Three engines. One chain. No manual script editing required.

| Engine | Platform | Output | Encodes? |
|--------|----------|--------|----------|
| FFmpeg | All | `-vf` filter chain | ✓ direct |
| VapourSynth | All | `.vpy` Python script | ✓ piped `vspipe → ffmpeg` (`--filter-engine vapoursynth`) |
| AviSynth+ | Windows | `.avs` script | — (script only) |

The **VapourSynth** engine is fully encodable — moist pipes `vspipe --y4m script.vpy - | ffmpeg …`,
which is what unlocks **`vs_superres`**: neural super-resolution via **vs-mlrt** (ONNX / TensorRT /
OpenVINO), the temporally-aware quality ceiling. It needs a VapourSynth + vs-mlrt + model install of
your own; moist generates the script and runs the pipe.

- **Visual filter chain editor** — drag, reorder, configure. See the generated script update live.
- **A working restoration toolkit** — crop, resize (bilinear→spline/lanczos), `bwdif`/`yadif`
  deinterlace, `nlmeans`/`hqdn3d` denoise, `deblock` (MPEG artifact removal), `cas`
  (contrast-adaptive sharpen), `unsharp`, `deband`, `eq`, tonemap, stabilize, and more
- **`superres` — GPU neural upscaling** via libplacebo (FSRCNNX for live action, Anime4K for
  anime). Runs the AI shader on the GPU, in the same FFmpeg pass; falls back to a spline scale
  automatically on machines without a Vulkan GPU
- **`--ai-upscale` — Real-ESRGAN deep-model upscaling**, the highest-quality option. Frame-based
  (extract → upscale → re-encode), so it's slow and disk-heavy — but it streams long sources in
  windows (bounded scratch, resumable), so it handles full films, not just clips. Auto-downloadable
- **`--fps <n>` — change the frame rate of anything.** `--fps-mode dup` (drop/duplicate, default),
  `motion` (motion-compensated `minterpolate`), or `rife` (**AI frame interpolation** — smooth
  24→60 or slow-mo, via the auto-downloadable RIFE tool)
- **30+ built-in presets** — professional, anime, restoration, upscaling, color grading, speed, creative
- **Save your own presets** — because nobody wants to rebuild a filter chain from memory

## Job System

> *"A man's gotta encode what a man's gotta encode."*

- **Dependency-aware queue** — Video + Audio encode in parallel, mux waits for both. Automatic.
- **Concurrent processing** — 1 to 32 simultaneous jobs. Your CPU, your rules.
- **Per-job processor isolation** — Each job gets its own processor instance. No shared state, no race conditions, no matter how many jobs you run at once.
- **Auto-start queue** — Enable in Settings and jobs start encoding the moment they're added
- **Real-time progress** — FPS, ETA, percentage for video; time-based progress for audio and muxing
- **Per-job cancellation** — Cancel one job without burning down the whole queue
- **Smart temp file cleanup** — Temp files preserved on failure for debugging; cleaned up on success

## Batch Processing

Drop your files. Walk away. Come back to finished encodes.

- **Drag-and-drop** — Drop a whole folder's worth of files at once
- **Per-file profile overrides** — Different encode settings per file in the same batch
- **Per-item status tracking** — Pending, Queued, Encoding, Completed, Failed
- **Flexible output paths** — Source directory or custom output with configurable suffix

## Target Quality — say what you want, not what you guess

CRF is a dial with no units. "CRF 23" means nothing about how the result will look;
it depends entirely on the content. So stop guessing:

```bash
moist encode input.mkv --target-vmaf 95
```

moist encodes a few short samples from across the file, measures each one, and
searches for the CRF that lands on your target — then encodes with it.

```
Searching for CRF that reaches Vmaf 95.00…
  [1/8] Trying CRF 26…
  [2/8] Trying CRF 18…
  [3/8] Trying CRF 22…
  ok   CRF 18  95.03
  low  CRF 19  94.26
  low  CRF 22  91.31
  low  CRF 26  85.25
  -> CRF 18 reaches 95.03 (target 95.00)
```

**Does sampling actually predict the whole file?** Measured, not assumed:

| | Predicted from 3×5s samples | Actual full encode |
|---|---|---|
| VMAF | 93.39 | **93.46** |
| Size | 64% of source | 61% |

The search is an interpolated bisection — it fits a line through the two bracketing
probes and jumps to where that line crosses the target, usually landing within a
point or two immediately. Results are cached per file, encoder, preset and target;
editing the source invalidates the entry by itself.

It always searches with the encoder and preset you'll actually encode with. Probing
with a fast GPU encoder and encoding with x265 would be quicker, but the CRF that
hits 95 on one encoder doesn't hit it on another — that's a fast wrong answer.

Works with `--target-xpsnr`, `--target-ssim`, `--target-psnr` too, and in the GUI via
the **Find CRF** button.

## Per-Scene Quality — the same quality everywhere, not the same effort

A single CRF gives every scene the same *effort*. That isn't the same as the same
*quality*: the easy scene comes out better than it needs to be, and the hard one comes
out worse than you wanted. Per-scene fixes that.

```bash
moist encode input.mkv --chunked --per-scene --target-vmaf 95
```

Every scene gets its own CRF, aimed at your target. Measured on a clip whose scenes span
a ~400× complexity range:

| | worst scene | mean VMAF | size |
|---|---|---|---|
| fixed CRF 25 | **83.55** | 93.40 | 1,557 KB |
| fixed CRF 17 | 93.82 | 96.89 | 3,089 KB |
| **per-scene** | 93.80 | 94.78 | **2,629 KB** |

Read that carefully, because the framing matters. Against fixed CRF 25 the per-scene
file is *bigger* — but CRF 25's average of 93.4 is hiding a scene sitting at **83.6**,
which is the one you'd actually notice. Pick a CRF low enough that no scene falls short
(CRF 17), and per-scene delivers the same worst-case quality **15% smaller**.

Quality spread across scenes drops from **13.9 VMAF to 3.6**.

Rather than searching every scene — which costs a full CRF search each — moist measures
scene complexity cheaply, searches a few that span the range, and models the rest. Then
it **checks its own predictions** and re-searches any that miss, because compressibility
and perceptual tolerance aren't the same thing: a test pattern compresses to nothing yet
falls apart when quantised hard. Use `--per-scene-full` to search every scene instead.

## Chunked Parallel Encoding

Split the file, encode the pieces at the same time, join them back:

```bash
moist encode input.mkv --chunked --jobs 8
```

Boundaries land on detected scene cuts, so they cost nothing — a keyframe was going
there anyway. **Interrupted encodes resume**: finished chunks are reused, and partial
ones left behind by a kill are detected and discarded rather than spliced in.

**Nothing is trusted.** FFmpeg will exit zero having written a file that's a few frames
short, so moist counts the frames in the joined result and fails if they don't match
the plan. Chunks are addressed in frame numbers rather than timestamps, because at
23.976 fps a duration in seconds can't address a frame boundary and the error
accumulates. Verified on a 40s 1080p clip: 1200 frames in, 1200 out, VMAF 60.15 chunked
vs 60.33 single-pass.

**How much faster?** Honestly: it depends entirely on whether your encoder already uses
all your cores. Measured on 16 cores:

| | Speedup |
|---|---|
| x265 `slow` | 1.19x (4 parallel encodes only manage 1.23x — x265 already saturates ~80% of the box) |
| SVT-AV1 preset 6 | 0.86x — *slower*, it threads well already |

Chunking earns its keep with encoders that thread poorly, with very high core counts,
and for resumability regardless of speed. It's off by default.

## Quality Metrics

Measure what you made, don't hope:

```bash
$ moist compare source.mkv encoded.mkv
Vmaf   93.46
       harmonic mean 93.43  (penalises bad scenes)
       worst frame   88.21
       best frame    99.12
       1,200 frames in 1m59s
```

- **VMAF, XPSNR, SSIM, PSNR** — all built into stock FFmpeg, no extra tooling
- **Pooled statistics** — a file can average 95 and still have a stretch that looks
  poor, so the worst frame and harmonic mean are reported alongside the average
- **Resolution-aware** — a downscaled encode is scaled back up before comparing, and
  4K content uses VMAF's 4K model
- `--measure` on any encode scores the result against its source when it finishes

## Hardware Encoding

Your GPU is sitting right there. Use it.

| | H.264 | H.265 | AV1 |
|---|---|---|---|
| **NVIDIA NVENC** | ✓ | ✓ | ✓ (RTX 40+) |
| **Intel Quick Sync** ⚗️ | ✓ | ✓ | ✓ (Arc / 12th gen+) |
| **AMD AMF** ⚗️ | ✓ | ✓ | ✓ (RX 7000+) |
| **Apple VideoToolbox** ⚗️ | ✓ | ✓ | — |
| **VA-API (Linux)** ⚗️ | ✓ | ✓ | ✓ |

⚗️ **Experimental.** NVENC is verified end-to-end on real hardware. Quick Sync, AMF,
VideoToolbox and VA-API are implemented from their documented FFmpeg interfaces and are
correctly *detected* (offered only where a test encode succeeds), but their encode paths
haven't been exercised on the actual hardware yet. Reports welcome.

moist doesn't take FFmpeg's word for what your machine can do. `ffmpeg -encoders` lists
everything the binary was *compiled* with — a stock Windows build claims NVENC, Quick
Sync, AMF and VA-API whether or not you own the hardware. moist encodes a few frames
through each one and only offers the ones that actually work, telling you why the rest
didn't:

```
$ moist list hardware
  ok  hw-nvenc-av1       AV1 (NVIDIA NVENC)
  ok  hw-nvenc-h264      H.264 (NVIDIA NVENC)
  ok  hw-nvenc-hevc      H.265 (NVIDIA NVENC)
  --  hw-qsv-h264        H.264 (Intel Quick Sync)
       Error creating a MFX session: -9.
  --  hw-amf-h264        H.264 (AMD AMF)
       DLL amfrt64.dll failed to open
```

**What it costs you.** 40 seconds of 1080p HEVC at CRF 26, preset slow, on an RTX 4080:

| | Time | Size | VMAF |
|---|---|---|---|
| NVENC | **4.9 s** | 37.5 MB | 74.16 |
| x265 | 85.1 s | 29.2 MB | 74.10 |

17× faster at the same perceptual quality — for a 28% larger file. Fixed-function
silicon skips the expensive search work software encoders do. Use it for previews and
bulk work; use x265 for masters.

## Subtitle Support

- **Copy all subtitles** from source with proper default/forced disposition
- **External subtitle import** — Add .srt/.ass files alongside the encode
- **Per-track metadata** — Language and title preserved through muxing

## Archival & Professional

- **Chroma control** — 4:2:0, 4:2:2 or 4:4:4, combined with bit depth. Asking for 4:4:4
  on a 10-bit master keeps it 10-bit rather than quietly flattening it
- **Subtitle burn-in** — render subtitles into the picture for players that can't show them
- **Per-track subtitle selection** — keep the tracks you want, not all or nothing
- **SVT-AV1 tuning** — `--av1-tune vq` for the perceptual tuning [SVT-AV1 4.0](https://wiki.x266.mov/docs/encoders/SVT-AV1)
  merged from the PSY fork; the encoder's own default optimises PSNR instead
- **HDR-aware** — HDR sources are detected and reported, and moist tells you *before*
  encoding if a setting will destroy something:

```
$ moist encode hdr.mkv --bit-depth 8 --dry-run
warning: This source is HDR but the output is forced to 8-bit. HDR needs 10-bit;
         the result will band badly. Use Auto or 10-bit.
```

Detection doesn't trust the container's colour tags — Matroska often reports
`color_transfer` as unknown while the bitstream is plainly PQ — so it reads frame side
data too, which is where mastering display, content light level, HDR10+ and Dolby Vision
actually live.

**Dynamic HDR passthrough (experimental, opt-in).** By default HDR10+ and Dolby Vision
dynamic metadata are *detected and warned about*: the static HDR10 base layer survives a
re-encode, so the result stays HDR, but the per-scene tone mapping doesn't. With
`--preserve-dynamic-hdr` (or the GUI toggle) moist extracts the metadata from the source with
`hdr10plus_tool` / `dovi_tool`, re-injects it into the encode and remuxes with mkvmerge. It has
been verified on synthetic Dolby Vision (profile 8.1) and HDR10+ test clips: every frame's
metadata survived (1137/1137). It still carries the experimental label until it's been
checked against commercial DV/HDR10+ masters. It needs both tools and MKVToolNix (all three
install from Settings on Windows), and it never touches the encode unless the whole round-trip
succeeds.

## Queue Survives a Crash

Queued encodes are recorded as they're queued and come back on the next launch — queued,
not auto-started, because returning to a machine already grinding through work you didn't
ask for would be worse than losing the queue.

Chunked encodes recover best: the chunk directory is keyed on the same settings, so a
restored run picks up every chunk the previous one finished rather than starting over.

## Nothing Gets Left Behind

A re-encode shouldn't quietly lose half your file.

- **Every audio track** — encode all of them, or pick a subset. Language and title ride along.
- **Chapters** — preserved through the mux, not dropped on the floor
- **Attachments** — subtitle fonts survive, so styled ASS still renders right
- **HDR10** — BT.2020 primaries, PQ transfer, mastering display and content light level all carry through
- **10-bit and 12-bit** — kept at their source depth unless you say otherwise

## Profiles

Sixteen built-in profiles, plus save your own:

| Profile | Video | Audio | Container |
|---------|-------|-------|-----------|
| **Restore SD to HD (vintage)** | x265 CRF 19, Medium, **10-bit** + restoration filter chain | Opus 192k | MKV |
| **AI Upscale SD to HD (GPU)** | x265 CRF 19, Medium, **10-bit** + restore chain with a **GPU neural upscale** (needs Vulkan) | Opus 192k | MKV |
| **AI Upscale SD to HD (Real-ESRGAN)** | x265 CRF 19, Medium, **10-bit** + **deep-model upscale** (needs the realesrgan tool; slow, for short/precious clips) | Opus 192k | MKV |
| **Broadcast 720p60 Film - Faithful 720p (B&W / Colour)** | x265 CRF 17, Medium, **10-bit, tune=grain**: native 59.94p and size kept, crop 88 px pillarbars, grayscale on B&W, CAS 0.3, gentle contrast curve, +5% saturation on colour. No upscale, no denoise — the broadcast look, tightened | **AC3 passthrough** | MKV |
| **Broadcast 720p60 Film - B&W to 1080p** | x265 CRF 18, Medium, **10-bit**: crop 88 px pillarbars → **inverse telecine** (bobbed 60p → 23.976) → grayscale → de-block → denoise → GPU neural upscale → CAS → deband. For MeTV-style over-the-air captures of film-sourced TV | Opus 160k | MKV |
| **Broadcast 720p60 Film - Colour to 1080p** | Same chain without the grayscale step | Opus 160k | MKV |
| **Broadcast 720p60 Film - … (Premium Real-ESRGAN)** | B&W and Colour variants with the **Real-ESRGAN deep-model upscale** in place of the GPU shader: sharpest texture, ~30× slower — for favourite episodes, not a whole library | Opus 160k | MKV |
| Web - H.265 High Quality | x265 CRF 22, Slow | Opus 128k | MKV |
| Web - H.264 Compatible | x264 CRF 20, Medium | AAC 192k | MP4 |
| Archive - Lossless | FFV1 | FLAC | MKV |
| YouTube Upload | x264 2-pass 15 Mbps, Slow, Film | AAC 320k | MP4 |
| Film - AV1 Efficient | SVT-AV1 CRF 30 | Opus 128k | MKV |
| Anime - H.265 Grain | x265 CRF 19, Slow, Animation | Opus 192k | MKV |
| Fast Preview | x264 CRF 28, Ultrafast | AAC 96k | MP4 |

Profiles store everything: video encoder, audio encoder, all codec-specific settings, output bit depth, normalization, audio-only mode, container format, and the full filter chain — which is how the one-click restore profile carries its whole pipeline.

## Plugin Marketplace

- **Local + online sources** — Add local plugin directories or browse online repositories
- **20+ curated plugins** — VapourSynth, AviSynth, FFmpeg ecosystem
- **Enable/disable sources** — Toggle without removing. Civilized.

## Theming

- **moist Dark** — The house skin. Blue-gray professional palette, low-contrast, easy on the eyes during long encode sessions
- **Fluent Dark** — Avalonia's built-in dark theme, if you prefer the stock look
- **Live switching** — Change themes in Settings, see it immediately. No restart required.

## Container Formats

MKV, MP4, WebM, AVI, TS, MOV

moist knows which codecs each container can actually carry and tells you *before* it burns CPU on an encode that can't be muxed. The rules were derived by muxing every combination and re-probing the result — not by reading a spec and hoping. Run `moist list containers` to see them.

## Tool Auto-Discovery & install

- **Automatic detection** — Scans PATH, common install locations, and bundled tool directories at startup
- **One-click install** — Download and install standalone tools from the GUI (Settings → Prerequisites)
  or the CLI: `moist tools install ffmpeg realesrgan rife`. Covers FFmpeg/FFprobe and the AI tools
  (Real-ESRGAN, RIFE) — self-contained binaries moist fetches, extracts, and wires up
- **Version detection** — Shows installed tool versions in the prerequisites panel
- **Missing tool warnings** — Clear startup warnings when required tools (FFmpeg, FFprobe) aren't found

> **VapourSynth + vs-mlrt** (the `vs_superres` ceiling) are a Python + plugin *ecosystem*, not a
> drop-in binary, so moist **detects** them and runs the `vspipe → ffmpeg` pipe once present, but
> doesn't auto-provision them — install VapourSynth with its own installer and drop vs-mlrt + your
> ONNX model into its plugins folder.

## Supported Encoders

### Video

| Encoder | ID | Notes |
|---------|----|-------|
| x264 via FFmpeg | `ffmpeg-x264` | H.264 — the reliable deputy |
| x265 via FFmpeg | `ffmpeg-x265` | H.265 — twice the efficiency, twice the patience |
| SVT-AV1 via FFmpeg | `ffmpeg-svtav1` | AV1 — the new sheriff in town (preset 0-13) |
| VP9 via FFmpeg | `ffmpeg-vp9` | WebM's finest |
| FFV1 Lossless | `ffmpeg-ffv1` | For the archivists who don't trust lossy |
| x264 Standalone CLI | `cli-x264` | Direct x264 via FFmpeg pipe |
| x265 Standalone CLI | `cli-x265` | Direct x265 via FFmpeg pipe |

### Audio

| Encoder | ID | Default | Notes |
|---------|----|---------|-------|
| Opus | `ffmpeg-audio-opus` | VBR 128k | Best general-purpose lossy codec. Just use this. |
| AAC | `ffmpeg-audio-aac` | VBR 192k | When compatibility matters more than quality |
| MP3 (LAME) | `ffmpeg-audio-mp3` | VBR 192k, scale 2 | For the nostalgic |
| FLAC | `ffmpeg-audio-flac` | Level 5 | Lossless — because bits matter |
| AC3 (Dolby) | `ffmpeg-audio-ac3` | CBR 384k | Dolby Digital surround |
| E-AC3 (Dolby+) | `ffmpeg-audio-eac3` | CBR 448k | Enhanced Dolby Digital Plus |
| Vorbis | `ffmpeg-audio-vorbis` | VBR 160k, Q3 | For WebM containers |
| Passthrough | `ffmpeg-audio-copy` | — | Copy audio without re-encoding |

---

## Built-in Filter Presets

### Professional
- Film Grain Reduction — NLMeans + compensating sharpen
- Heavy Denoise — HQDN3D + NLMeans combo for noisy sources
- Sharpen Soft Sources — Moderate unsharp for webcam/camcorder
- Broadcast Deinterlace — Yadif with spatial check
- DVD Restoration — Deinterlace + denoise + upscale to 720p
- Video Stabilization — vidstabtransform smoothing
- Broadcast Safe Colors — Limiter for TV-legal levels
- Low Light Enhancement — Brightness + gamma + heavy denoise

### Upscaling
- Modern Upscale to 4K — Lanczos5 + adaptive sharpening
- Upscale SD to 1080p — Pre-denoise + Lanczos + edge sharpening
- Upscale 2x Spline36 — Balanced sharpness/ringing tradeoff

### Anime
- Anime Processing — Light denoise + line art sharpening
- Anime Upscale 720p to 1080p — Lanczos + gentle sharpen
- Anime Deband + Clean — Deband + light temporal denoise

### Restoration
- VHS Capture Restoration — Full pipeline: deinterlace, denoise, color fix, sharpen
- Archival Preservation — Minimal processing for digitization

### Color / Creative / Speed / Web
Color grading, vintage looks, slow motion, speed-ups, web optimization, social media crops — 30+ presets in total. Or save your own.

---

## Configuration

Settings are stored as JSON:
- **Linux/macOS**: `~/.config/moist/settings.json`
- **Windows**: `%APPDATA%/moist/settings.json`

Profiles are stored as individual JSON files:
- **Linux/macOS**: `~/.config/moist/profiles/*.json`
- **Windows**: `%APPDATA%/moist/profiles/*.json`

### Tool Paths
FFmpeg, FFprobe, MKVMerge, x264, x265, VapourSynth — configurable in the Settings tab or auto-discovered at startup.

### Options
- Max concurrent jobs (1-32)
- Auto-start queue on enqueue
- Delete temp files after successful completion
- **Default output directory** and **temp / scratch directory**
- **Ask where to save each time** — prompt for the output location instead of using the default folder
- **Output name suffix** — what gets appended to an auto-named output (default `_encoded`)
- **Reveal folder when done** — open the destination when a job finishes
- Theme selection (moist Dark / Fluent Dark)
