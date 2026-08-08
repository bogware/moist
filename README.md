# moist

**Modern Open-source Intelligent Streaming Transcoder**

> *"There's two kinds of people in this world: those who encode their video right, and those who wish they had."*

A cross-platform video encoder GUI built with C# / .NET 8 / [Avalonia UI](https://avaloniaui.net/). moist provides a professional-grade frontend for video encoding, audio encoding, filtering, and muxing — the kind of tool a man builds when he's tired of doing things the hard way.

It won't judge your source material. It'll just make it better.

---

## Bring the old tapes back to life — SD → HD restoration

> *"Everything worth keepin' gets old. Don't mean you let it rot."*

That shelf of ripped DVDs and old fight tapes — 512×384, soft, blocky, dark — moist
will bring up to sharp 1080p in one command. There's a built-in profile that does the
whole job:

```bash
moist encode "UFC 01.avi" -o "UFC 01 - Restored.mkv" --profile "Restore SD to HD"
```

That's the whole draw. The profile carries the full restoration pipeline and encodes
HEVC 10-bit. In the GUI, pick **Restore SD to HD (vintage)** from the profile dropdown
and hit encode.

**What it does — and why the order matters.** You repair the picture at its native size,
*then* enlarge it, *then* sharpen. Any other order and you just upscale the dirt:

| # | Stage | Filter | Why |
|---|-------|--------|-----|
| 1 | De-block | `deblock` (spp) | Old DivX/XviD/DVD rips are riddled with DCT block noise in crowds and shadows. Clean it first — everything downstream rides on it. |
| 2 | Denoise | `hqdn3d` | A light, motion-safe pass. Keeps real detail; no ghosting on fast action. |
| 3 | Upscale | `resize` (spline) | Scale to 1080p *after* the picture is clean. `-2:1080` keeps the source shape — a 4:3 tape lands at 1440×1080. |
| 4 | Sharpen | `cas` | Contrast-adaptive sharpening crisps edges after the upscale, without the halos an unsharp mask leaves behind. |
| 5 | Deband | `deband` | Smooths the banding that low-bitrate gradients (dark arenas, skies) fall into — especially before a 10-bit encode. |

Measured on real 2000-era fight tapes, the encode lands **VMAF ~98** against a
near-lossless master at about a third of the bitrate, and NVENC on a modern GPU runs it
faster than real time.

**Tips from the trail:**

- **Fast vs. archival.** The profile ships with x265 so it runs anywhere. For the
  whole-library grind, hand it to the GPU: `--video-encoder hw-nvenc-hevc --mode quality
  --crf 24`. For a keepsake master, `--video-encoder ffmpeg-x265 --preset slow`.
- **Interlaced source?** These tapes were progressive. Check yours with `moist probe`;
  if it says interlaced, put a deinterlacer at the front of the chain
  (`--filter bwdif`).
- **It's restoration, not resurrection.** moist cleans, scales and sharpens with
  classical filters — it invents nothing. A motion-blurred face in the source stays soft;
  that blur was baked in the day it was shot.

---

## Features

### Video Encoding

The heart of the operation. No shortcuts, no compromises.

- **Video Codecs**: x264, x265, SVT-AV1, VP9, FFV1 — via FFmpeg or standalone CLI
- **Hardware Encoding**: NVENC, Quick Sync, AMF, VideoToolbox and VA-API (H.264 / H.265 / AV1)
- **Bit Depth**: 8, 10 or 12-bit, or Auto to follow the source — 10-bit HDR masters stay 10-bit
- **Encoding Modes**: CRF, constant quality, single-pass bitrate, two-pass bitrate
- **Full Preset & Tune Control**: Every x264/x265/SVT-AV1 preset and tune option exposed
- **SVT-AV1 Numeric Preset**: Direct 0-13 preset control for fine-grained AV1 speed/quality tradeoff
- **Two-Pass Encoding**: Automatic two-pass handling for FFmpeg and standalone CLI encoders
- **Custom Parameters**: Pass arbitrary encoder arguments for those who know what they're doing

### Audio Encoding

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

### Filter Engine

Three engines. One chain. No manual script editing required.

| Engine | Platform | Output |
|--------|----------|--------|
| FFmpeg | All | `-filter_complex` argument string |
| VapourSynth | All | `.vpy` Python script |
| AviSynth+ | Windows | `.avs` script |

- **Visual filter chain editor** — drag, reorder, configure. See the generated script update live.
- **A working restoration toolkit** — crop, resize (bilinear→spline/lanczos), `bwdif`/`yadif`
  deinterlace, `nlmeans`/`hqdn3d` denoise, `deblock` (MPEG artifact removal), `cas`
  (contrast-adaptive sharpen), `unsharp`, `deband`, `eq`, tonemap, stabilize, and more
- **30+ built-in presets** — professional, anime, restoration, upscaling, color grading, speed, creative
- **Save your own presets** — because nobody wants to rebuild a filter chain from memory

### Job System

> *"A man's gotta encode what a man's gotta encode."*

- **Dependency-aware queue** — Video + Audio encode in parallel, mux waits for both. Automatic.
- **Concurrent processing** — 1 to 32 simultaneous jobs. Your CPU, your rules.
- **Per-job processor isolation** — Each job gets its own processor instance. No shared state, no race conditions, no matter how many jobs you run at once.
- **Auto-start queue** — Enable in Settings and jobs start encoding the moment they're added
- **Real-time progress** — FPS, ETA, percentage for video; time-based progress for audio and muxing
- **Per-job cancellation** — Cancel one job without burning down the whole queue
- **Smart temp file cleanup** — Temp files preserved on failure for debugging; cleaned up on success

### Batch Processing

Drop your files. Walk away. Come back to finished encodes.

- **Drag-and-drop** — Drop a whole folder's worth of files at once
- **Per-file profile overrides** — Different encode settings per file in the same batch
- **Per-item status tracking** — Pending, Queued, Encoding, Completed, Failed
- **Flexible output paths** — Source directory or custom output with configurable suffix

### Target Quality — say what you want, not what you guess

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

### Per-Scene Quality — the same quality everywhere, not the same effort

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

### Chunked Parallel Encoding

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

### Quality Metrics

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

### Hardware Encoding

Your GPU is sitting right there. Use it.

| | H.264 | H.265 | AV1 |
|---|---|---|---|
| **NVIDIA NVENC** | ✓ | ✓ | ✓ (RTX 40+) |
| **Intel Quick Sync** | ✓ | ✓ | ✓ (Arc / 12th gen+) |
| **AMD AMF** | ✓ | ✓ | ✓ (RX 7000+) |
| **Apple VideoToolbox** | ✓ | ✓ | — |
| **VA-API (Linux)** | ✓ | ✓ | ✓ |

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

### Subtitle Support

- **Copy all subtitles** from source with proper default/forced disposition
- **External subtitle import** — Add .srt/.ass files alongside the encode
- **Per-track metadata** — Language and title preserved through muxing

### Archival & Professional

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

**Honest limitation:** HDR10+ and Dolby Vision dynamic metadata are *detected and warned
about* but not yet preserved through a re-encode. The static HDR10 base layer survives, so
the result stays HDR; the per-scene tone mapping doesn't. Remux instead of re-encoding to
keep it.

### Queue Survives a Crash

Queued encodes are recorded as they're queued and come back on the next launch — queued,
not auto-started, because returning to a machine already grinding through work you didn't
ask for would be worse than losing the queue.

Chunked encodes recover best: the chunk directory is keyed on the same settings, so a
restored run picks up every chunk the previous one finished rather than starting over.

### Nothing Gets Left Behind

A re-encode shouldn't quietly lose half your file.

- **Every audio track** — encode all of them, or pick a subset. Language and title ride along.
- **Chapters** — preserved through the mux, not dropped on the floor
- **Attachments** — subtitle fonts survive, so styled ASS still renders right
- **HDR10** — BT.2020 primaries, PQ transfer, mastering display and content light level all carry through
- **10-bit and 12-bit** — kept at their source depth unless you say otherwise

### Profiles

Eight built-in presets ready to ride, plus save your own:

| Profile | Video | Audio | Container |
|---------|-------|-------|-----------|
| **Restore SD to HD (vintage)** | x265 CRF 19, Medium, **10-bit** + restoration filter chain | Opus 192k | MKV |
| Web - H.265 High Quality | x265 CRF 22, Slow | Opus 128k | MKV |
| Web - H.264 Compatible | x264 CRF 20, Medium | AAC 192k | MP4 |
| Archive - Lossless | FFV1 | FLAC | MKV |
| YouTube Upload | x264 2-pass 15 Mbps, Slow, Film | AAC 320k | MP4 |
| Film - AV1 Efficient | SVT-AV1 CRF 30 | Opus 128k | MKV |
| Anime - H.265 Grain | x265 CRF 19, Slow, Animation | Opus 192k | MKV |
| Fast Preview | x264 CRF 28, Ultrafast | AAC 96k | MP4 |

Profiles store everything: video encoder, audio encoder, all codec-specific settings, output bit depth, normalization, audio-only mode, container format, and the full filter chain — which is how the one-click restore profile carries its whole pipeline.

### Plugin Marketplace

- **Local + online sources** — Add local plugin directories or browse online repositories
- **20+ curated plugins** — VapourSynth, AviSynth, FFmpeg ecosystem
- **Enable/disable sources** — Toggle without removing. Civilized.

### Theming

- **moist Dark** — The house skin. Blue-gray professional palette, low-contrast, easy on the eyes during long encode sessions
- **Fluent Dark** — Avalonia's built-in dark theme, if you prefer the stock look
- **Live switching** — Change themes in Settings, see it immediately. No restart required.

### Container Formats

MKV, MP4, WebM, AVI, TS, MOV

moist knows which codecs each container can actually carry and tells you *before* it burns CPU on an encode that can't be muxed. The rules were derived by muxing every combination and re-probing the result — not by reading a spec and hoping. Run `moist list containers` to see them.

### Command Line

Everything the GUI does, scriptable. The CLI and the GUI share the same pipeline code, so they produce identical output.

```bash
moist encode input.mkv -o output.mp4 --crf 20 --preset slow
moist encode input.mkv --profile "Web - H.265 High Quality"

# one-click: restore an old SD tape to sharp 10-bit 1080p (filters + upscale from the profile)
moist encode "UFC 01.avi" -o "UFC 01 - Restored.mkv" --profile "Restore SD to HD"
moist encode input.mkv --audio-only --audio-encoder ffmpeg-audio-flac
moist encode input.mkv --mode twopass --bitrate 8000 --container mp4
moist encode input.mkv --filter resize:width=1920,height=1080 --filter unsharp:luma_amount=1.2

# say what quality you want instead of guessing a CRF
moist encode input.mkv --target-vmaf 95 --measure
moist crf-search input.mkv --target-vmaf 95 --json

# hardware, all audio tracks, forced 10-bit
moist encode input.mkv --video-encoder hw-nvenc-hevc --audio-track all --bit-depth 10

# split into parallel chunks; resumes if interrupted
moist encode input.mkv --chunked --jobs 8

# every scene gets its own CRF, aimed at the same quality throughout
moist encode input.mkv --chunked --per-scene --target-vmaf 95

moist batch *.mkv --container mp4 --output-dir ./out --jobs 4 --keep-going
moist compare source.mkv encoded.mkv --metric vmaf
moist probe input.mkv --json
moist list encoders | hardware | containers | profiles | filters
moist tools
```

### Automation

```bash
# encode whatever lands in a folder, with any encode option you like
moist watch ./inbox --output-dir ./encoded --crf 22 --preset slow
moist watch /mnt/dropbox --output-dir /mnt/library -r --on-success move --move-to ./done

# find damaged files; exit code 1 if any, so it can drive a scheduled check
moist health /media/films
moist health /media/films --deep --json > report.json

# HTTP API over the same queue
moist serve --port 7788
```

A file that has just appeared in a watch folder is usually still being copied — it
has a valid header and a plausible duration the whole way through, so encoding it
early produces a truncated result with nothing to explain it. `moist watch` waits
for a file to stop changing first, remembers what it has already encoded across
restarts, and only deletes or moves a source once its encode has succeeded.

`moist health` without `--deep` reads container headers, which catches an
interrupted write but not damage inside an otherwise complete file — that has a
perfectly good header too. `--deep` decodes everything and compares the frames it
actually got against what the container claims.

`moist serve` can read and write files anywhere the server process can, so it
authenticates by default, binds to loopback by default, and refuses to do neither.
Submitted options are parsed by the same code as the command line, so
`{"crf": 22}` means exactly what `--crf 22` means.

### Docker

```bash
docker build -t moist .
docker run -p 7788:7788 -v /media:/media -e MOIST_TOKEN=secret moist
docker run --rm -v /media:/media moist health /media --deep
```

Debian's FFmpeg has x264, x265 and VP9 but **not** libsvtav1 or libvmaf, so AV1
encoding and `--target-vmaf` are unavailable in the stock image. `moist tools`
reports what was actually found.

- `--dry-run` prints the plan (including the exact FFmpeg commands) without encoding
- `--json` emits machine-readable results; logs go to stderr so stdout stays clean for pipes
- Exit codes: `0` success, `1` encode failed, `2` bad usage, `3` missing prerequisite
- `Ctrl+C` cancels gracefully and kills the FFmpeg process tree

### Tool Auto-Discovery

- **Automatic detection** — Scans PATH, common install locations, and bundled tool directories at startup
- **One-click install** — Download and install FFmpeg, FFprobe, and other tools directly from Settings
- **Version detection** — Shows installed tool versions in the prerequisites panel
- **Missing tool warnings** — Clear startup warnings when required tools (FFmpeg, FFprobe) aren't found

---

## Installation

> *"A carpenter's only as good as his saw. FFmpeg's the saw."*

### The quick way — grab a build

Head to the [Releases](https://github.com/bogware/moist/releases) page and pull the
archive for your platform. The builds are self-contained — no runtime to chase down.

- **Windows x64** — `moist-*-win-x64.zip` (GUI) or `moist-cli-*-win-x64.zip` (command line). Unzip, run `moist.exe`.
- **Linux x64** — `moist-*-linux-x64.tar.gz`. Extract, `chmod +x moist`, run it.
- **macOS** — build from source for now (see [Quick Start](#quick-start) below).

### You'll need FFmpeg

moist shells out to FFmpeg and FFprobe — it's the one thing it can't ride without.
Either let moist fetch them for you (**Settings → Prerequisites → Install**), or put them
on your PATH:

| OS | Command |
|----|---------|
| Windows | `winget install Gyan.FFmpeg` — or `choco install ffmpeg-full` |
| macOS | `brew install ffmpeg` |
| Debian / Ubuntu | `sudo apt install ffmpeg` |
| Fedora | `sudo dnf install ffmpeg` |
| Arch | `sudo pacman -S ffmpeg` |

Get a **full** build if you can. The stock Debian package has no libsvtav1 or libvmaf, so
AV1 encoding and `--target-vmaf` go dark. `moist tools` prints exactly what your FFmpeg
was built with, so you're never guessing.

---

## Quick Start

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- [FFmpeg](https://ffmpeg.org/download.html) and FFprobe on your PATH (or let moist install them for you)
- Optional: [x264](https://www.videolan.org/developers/x264.html), [x265](https://www.videolan.org/developers/x265.html) for standalone CLI encoding
- Optional: [VapourSynth](https://www.vapoursynth.com/) for VapourSynth filters
- Optional: [MKVToolNix](https://mkvtoolnix.download/) for MKVMerge muxing

### Build & Run

```bash
git clone https://github.com/bogware/moist.git
cd moist/src
dotnet restore
dotnet build
dotnet run --project Moist.App                                # GUI
dotnet run --project Moist.Cli -- encode input.mkv -o out.mp4 # CLI
```

### Release Build

```bash
cd src
dotnet publish Moist.App -c Release -o ../dist
```

### Run Tests

```bash
cd src
dotnet test                        # everything
dotnet test Moist.Core.Tests       # fast unit tests only
dotnet test Moist.IntegrationTests # real FFmpeg encodes
```

**255 unit tests** covering job models, queue behavior, encoder registry, profiles, settings, filter engines (including the restoration filters and profile-carried filter chains), container/extension mapping, audio processor argument building, hardware encoder plumbing, quality metric parsing, search arithmetic, chunk planning, the per-scene complexity model, and CLI parsing — none of which launch an external process.

**122 integration tests** that run real encodes through the full pipeline and verify the output with ffprobe: every video encoder, every audio codec, every container, 10-bit and HDR sources, silent sources, multi-track audio, chapters and attachments, chroma and subtitle handling, filter chains, cancellation, concurrency, awkward filenames, temp-file cleanup, all four quality metrics, target-quality search, chunked encoding, per-scene optimisation, and queue persistence — including a test that searches then encodes the full file and asserts the sampled prediction held, one that deliberately truncates a chunk to prove the frame-count verification catches it, one that checks per-scene beats a fixed CRF chosen for the hardest scene, and one that measures the picture to prove burned-in subtitles were actually rendered rather than merely not erroring. They skip automatically when FFmpeg isn't on your PATH (and hardware tests skip without a usable GPU), so check the skip count before trusting a green run.

If FFmpeg isn't on your PATH, set the path in the **Settings** tab after launching. moist won't hold it against you.

---

## Architecture

> *"Out here, a man builds things to last — or he doesn't build 'em at all."*

### Four-Project Structure

```
src/
 Moist.Core/            Core library — no UI dependency
    Jobs/               Job queue, workers, processors, two-pass
    Encoders/           Video + Audio encoder abstractions & settings
    Muxers/             Container muxing + codec/container compatibility
    Media/              FFprobe analysis, subtitle/audio stream parsing
    Settings/           JSON-based app settings
    Profiles/           Encoding profile save/load (7 built-in + user profiles)
    Tools/              Tool discovery, version detection, auto-download
    Plugins/            Plugin interface + assembly loader
 Moist.Filters/         Filter engine abstraction layer
    Engines/            FFmpeg, VapourSynth, AviSynth implementations
    FilterChain         Ordered, engine-agnostic filter chain
    FilterPreset        Built-in + user presets with JSON persistence
    PluginSource        Plugin source management + marketplace catalog
 Moist.Services/        Shared composition root — UI-free
    AppServices         Service wiring, encoder/processor registration
    EncodeJobBuilder    Settings -> job graph. The one pipeline both frontends use.
 Moist.App/             Avalonia GUI application
    ViewModels/         MVVM ViewModels (CommunityToolkit.Mvvm)
    Views/              AXAML views with compiled bindings
    Themes/             Skinnable theme overlays (MoistDark, etc.)
 Moist.Cli/             'moist' command-line binary — no Avalonia dependency
 Moist.Core.Tests/      Fast unit tests (255) — never launch a process
 Moist.IntegrationTests/ Real FFmpeg encodes verified with ffprobe (skip without FFmpeg)
```

The GUI and the CLI are both thin shells over `Moist.Services`. Neither contains encoding
logic of its own — they build an `EncodeRequest` and hand it to `EncodeJobBuilder`, which is
why the two front-ends can't drift apart.

### Key Design Patterns

| Pattern | Description |
|---------|-------------|
| **MVVM** | ViewModels drive UI via data binding. Views are pure AXAML. |
| **Job Queue with Dependencies** | Jobs declare dependencies. Video+Audio run in parallel, mux waits for both. |
| **Per-Job Processor Factories** | Each job gets a fresh processor instance via factory — no shared mutable state between concurrent jobs. |
| **CommandLineProcessor** | Base class for all CLI tool integrations. Async with progress parsing from stdout/stderr. |
| **TwoPassProcessor** | Wraps any video processor for two-pass bitrate mode with snapshot/restore of job state. |
| **Encoder Registry** | Dynamic registration of encoders via factory pattern (`Func<IJobProcessor>`). |
| **Filter Chain** | Engine-agnostic filter definitions translated to engine-specific scripts. |
| **Theme Overlay** | Additive style system — skins layer on top of FluentTheme without replacing it. |

### Encoding Pipeline

```
User configures settings
        |
        v
  [Add to Queue]
        |
        +-- VideoJob (video encode -> temp .mkv)
        |     +-- with FilterScript from filter chain
        |     +-- with -tune for x264/x265/VP9
        |
        +-- AudioJob (audio encode -> temp .mka)
        |     +-- selected stream, normalization filters
        |     +-- codec-specific settings (Opus/AAC/MP3/FLAC/AC3/E-AC3/Vorbis)
        |
        +-- MuxJob (depends on video + audio)
              +-- video + audio + subtitles -> final .mkv/.mp4/.webm

  Audio-Only Mode:
        |
        +-- AudioJob (audio encode -> final .opus/.m4a/.mp3/.flac)
              +-- no video job, no mux job
```

---

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

---

## Developer Guide

### Code Conventions

- **C# 12** / .NET 8, nullable enabled, implicit usings
- **MVVM** via `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`)
- **Avalonia AXAML** with `x:DataType` (compiled bindings)
- PascalCase for public members, `_camelCase` for private fields
- File-scoped namespaces (`namespace Moist.Core.Jobs;`)
- Async-first: `Task` + `CancellationToken` everywhere
- JSON settings with `JsonStringEnumConverter` for human-readable enums
- Constructor injection for services in ViewModels

### Adding a Video Encoder

1. Create settings class in `Moist.Core/Encoders/Video/`
2. Register in `AppServices.cs` via `RegisterVideoEncoder()` — the id must be `ffmpeg-{codec}`
3. Handle in `EncodeJobBuilder.Build()` switch
4. Map it in `ContainerCompatibility.StreamCodecForEncoder()` and add it to the container tables

### Adding an Audio Encoder

1. Create settings class extending `AudioEncoderSettings` in `Moist.Core/Encoders/Audio/`
2. Register in `AppServices.cs` via `RegisterAudioEncoder()` — the id must be `ffmpeg-audio-{codec}`
3. Add codec-specific args in `FFmpegAudioProcessor.GetCodecSpecificArgs()`
4. Handle in `EncodeJobBuilder.CreateAudioSettings()` and `AudioFileExtension()`
5. Map it in `ContainerCompatibility` so container validation knows about it

Both front-ends pick these up automatically — there's nothing to add to the GUI or the CLI.

### Adding a Filter

1. Add `FilterDefinition` to the appropriate engine in `Moist.Filters/Engines/`
2. Add the build method in `GenerateScript`
3. Optionally add a preset in `FilterPresetManager.AddBuiltInPresets()`

### Adding a Theme

1. Create a new `.axaml` styles file in `Moist.App/Themes/`
2. Add it as a `StyleInclude` in `App.axaml`
3. Register the theme name in `SettingsTabViewModel.ThemeOptions`
4. Handle toggle logic in `App.axaml.cs`

### Key Files

| Task | Files |
|------|-------|
| Video encoder | `Core/Encoders/Video/` + `AppServices.cs` + `EncodeTabViewModel.cs` |
| Audio encoder | `Core/Encoders/Audio/` + `AppServices.cs` + `EncodeTabViewModel.cs` |
| Filter | `Filters/Engines/` + `FilterPreset.cs` |
| UI tab | `App/Views/*.axaml` + `ViewModels/*.cs` |
| Settings | `Core/Settings/MoistSettings.cs` + `SettingsTabViewModel.cs` |
| Theme | `App/Themes/` + `App.axaml` + `App.axaml.cs` |
| Job pipeline | `Core/Jobs/` + `AppServices.RegisterDefaultProcessors()` |
| Tests | `Core.Tests/` (fast, xUnit) + `IntegrationTests/` (real encodes) |

---

## CI/CD

- **CI** (`.github/workflows/ci.yml`): Builds Debug + Release and runs xUnit tests on Windows and Linux via self-hosted runners on every push/PR to `main`.
- **Release** (`.github/workflows/release.yml`): Triggered on `v*` tags. Produces self-contained Windows x64 (ZIP) and Linux x64 (tar.gz) builds. Creates GitHub releases with all artifacts.

---

## Tips, Tricks & Hard-won Hints

> *"Experience is the comb life hands you after you've already lost your hair."*

- **Stop guessing at CRF.** `--target-vmaf 93` samples the file and finds the CRF that hits
  your quality target. A number you can reason about beats a dial with no units.
- **NVENC for bulk, x265 for masters.** Fixed-function silicon is ~15× faster at the same
  look for a slightly bigger file. Previews and the whole-library grind → NVENC. The one
  you'll keep forever → x265 `slow`.
- **10-bit from an 8-bit source isn't snake oil.** It gives the encoder headroom and kills
  banding in gradients — clean skies, clean fades — for a little size. The restore profile
  does it for you.
- **Chunked encoding is no free lunch.** It only recovers cores your encoder wasn't already
  using. x265 `slow` already saturates a big box; SVT-AV1 threads itself. It earns its keep
  with poorly-threaded encoders, huge core counts, and resumability.
- **The output extension picks the container.** `-o out.mp4` gives you MP4, `-o out.mkv`
  gives you Matroska — no `--container` needed. moist checks the codec fits the box *before*
  it burns an encode, so a doomed pair fails in a second, not an hour.
- **Let a folder do the work.** `moist watch ./inbox --output-dir ./done --profile "Restore
  SD to HD"` encodes anything dropped in, waits for the copy to finish, and survives a reboot.
- **Look the tapes over, before and after.** `moist probe file.avi` shows resolution, fields,
  and bit depth. `moist health ./library --deep` decodes every file and flags the ones rotting
  from the inside.
- **`--dry-run` shows the plan.** Every job and the exact FFmpeg command, without encoding a
  single frame. Look before you leap.

---

## FAQ

**Does moist upscale with AI, like Topaz?**
No. moist restores with classical, deterministic filters — de-block, denoise, high-quality
scaling, adaptive sharpen, deband. It cleans and enlarges what's there; it doesn't invent
detail. Fast, repeatable, and honest about what it can't do.

**Which video encoder should I use?**
x265 (`ffmpeg-x265`) for quality masters. NVENC (`hw-nvenc-hevc`) when you've got an NVIDIA
GPU and a stack of files. SVT-AV1 (`ffmpeg-svtav1`) for the smallest files if you don't mind
the wait. x264 (`ffmpeg-x264`) when a device won't play anything newer.

**What CRF should I use?**
Don't. Use `--target-vmaf 93` (good) or `95` (near-transparent) and let moist find it. If you
insist: x265 CRF 18–20 is visually excellent for most content.

**My `.mp4` came out as Matroska, or my codec got rejected.**
Older builds ignored the output extension. Current moist reads it: `-o out.mp4` is MP4,
`-o out.avi` is AVI, and it warns up front if the codec can't live in that container.

**NVENC (or Quick Sync / AMF) doesn't show up.**
moist test-encodes each hardware path and only offers the ones that actually work. Run
`moist list hardware` — it prints why each missing one failed. A line in `ffmpeg -encoders`
means the binary was *compiled* with it, not that your machine can run it. Update your GPU
driver and try again.

**My restored file looks great, but faces in motion are still soft.**
That's motion blur baked into the source frame — no filter brings back detail the tape never
recorded. Static shots (graphics, standing figures, crowds) sharpen up dramatically; fast
motion stays as sharp as the original exposure allowed.

**4:3 or 16:9 — will the restore profile stretch my video?**
No. The upscale targets 1080p *height* and keeps the source shape. A 4:3 tape becomes
1440×1080; a 16:9 one becomes 1920×1080.

**Can I run it headless — on a server or in Docker?**
Yes. `moist serve` exposes the queue over HTTP (authenticated, loopback by default), and the
Docker image runs it out of the box. The CLI does everything the GUI does.

---

## Troubleshooting

**FFmpeg not found** — Set the path in Settings, or put `ffmpeg` and `ffprobe` on your PATH. Or click "Install" in the Settings prerequisites panel.

**Build errors after pulling** — `cd src && dotnet restore && dotnet build`

**Encoding fails with "No processor found"** — The selected encoder's tool isn't installed or the path isn't configured in Settings.

**Audio-only output has wrong extension** — The extension auto-updates when you toggle Audio Only mode. If you set the output path manually, change the extension to match the codec (.opus, .m4a, .mp3, .flac).

**Theme not applying** — Click "Save Settings" after changing the theme. It applies on save.

**Two-pass encoding produces no output** — Check that the output directory is writable and has enough disk space. Two-pass creates temporary log files next to the output.

**Hardware encoder missing from the list** — moist only offers GPU encoders that pass a real test-encode. `moist list hardware` shows why each absent one failed (no driver, no silicon, disabled in the build). NVENC needs a reasonably current NVIDIA driver.

**Full-length x265 encode takes hours** — 1080p at `slow` is genuinely heavy work. Drop to `--preset medium`, or hand it to the GPU with `--video-encoder hw-nvenc-hevc`, for anything short of a final master.

**Output has visible banding** — encode 10-bit (`--bit-depth 10`) and add `--filter deband`. Banding is an 8-bit gradient problem; 10-bit gives the encoder room. The "Restore SD to HD" profile already does both.

**Watch folder keeps re-encoding the same file** — keep the output directory *outside* the watched folder. moist refuses an output dir nested inside the watch path for exactly this reason, and a move-destination inside it will loop.

**Restore profile came out the wrong size** — it targets 1080p height and keeps aspect. If a source is anamorphic (stored 720×480 but meant for 16:9), set the size yourself: `--filter resize:width=1920,height=1080`.

---

## Contributing

1. Fork it
2. Branch it: `git checkout -b feature/my-feature`
3. Build it: `dotnet build`
4. Test it: `dotnet test` (all tests must pass)
5. Commit it with a clear message
6. Open a Pull Request

---

## License

See [LICENSE](LICENSE) for details.

---

> *"Some folks encode with fear. Some encode with reckless abandon. The smart ones use moist."*
>
> *— The kind of quiet truth a man learns after a few thousand encodes*

*moist — because your encodes should be fresh.*
