# moist

**Modern Open-source Intelligent Streaming Transcoder**

> *"There's two kinds of people in this world: those who encode their video right, and those who wish they had."*

A cross-platform video encoder GUI built with C# / .NET 8 / [Avalonia UI](https://avaloniaui.net/). moist provides a professional-grade frontend for video encoding, audio encoding, filtering, and muxing — the kind of tool a man builds when he's tired of doing things the hard way.

It won't judge your source material. It'll just make it better.

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

### Nothing Gets Left Behind

A re-encode shouldn't quietly lose half your file.

- **Every audio track** — encode all of them, or pick a subset. Language and title ride along.
- **Chapters** — preserved through the mux, not dropped on the floor
- **Attachments** — subtitle fonts survive, so styled ASS still renders right
- **HDR10** — BT.2020 primaries, PQ transfer, mastering display and content light level all carry through
- **10-bit and 12-bit** — kept at their source depth unless you say otherwise

### Profiles

Seven built-in presets ready to ride, plus save your own:

| Profile | Video | Audio | Container |
|---------|-------|-------|-----------|
| Web - H.265 High Quality | x265 CRF 22, Slow | Opus 128k | MKV |
| Web - H.264 Compatible | x264 CRF 20, Medium | AAC 192k | MP4 |
| Archive - Lossless | FFV1 | FLAC | MKV |
| YouTube Upload | x264 2-pass 15 Mbps, Slow, Film | AAC 320k | MP4 |
| Film - AV1 Efficient | SVT-AV1 CRF 30 | Opus 128k | MKV |
| Anime - H.265 Grain | x265 CRF 19, Slow, Animation | Opus 192k | MKV |
| Fast Preview | x264 CRF 28, Ultrafast | AAC 96k | MP4 |

Profiles store everything: video encoder, audio encoder, all codec-specific settings, normalization, audio-only mode, container format, and filter chain.

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
moist encode input.mkv --audio-only --audio-encoder ffmpeg-audio-flac
moist encode input.mkv --mode twopass --bitrate 8000 --container mp4
moist encode input.mkv --filter resize:width=1920,height=1080 --filter unsharp:luma_amount=1.2

# say what quality you want instead of guessing a CRF
moist encode input.mkv --target-vmaf 95 --measure
moist crf-search input.mkv --target-vmaf 95 --json

# hardware, all audio tracks, forced 10-bit
moist encode input.mkv --video-encoder hw-nvenc-hevc --audio-track all --bit-depth 10

moist batch *.mkv --container mp4 --output-dir ./out --jobs 4 --keep-going
moist compare source.mkv encoded.mkv --metric vmaf
moist probe input.mkv --json
moist list encoders | hardware | containers | profiles | filters
moist tools
```

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

**138 unit tests** covering job models, queue behavior, encoder registry, profiles, settings, filter engines, audio processor argument building, hardware encoder plumbing, quality metric parsing, search arithmetic, and CLI parsing — none of which launch an external process.

**79 integration tests** that run real encodes through the full pipeline and verify the output with ffprobe: every video encoder, every audio codec, every container, 10-bit and HDR sources, silent sources, multi-track audio, chapters and attachments, filter chains, subtitles, cancellation, concurrency, awkward filenames, temp-file cleanup, all four quality metrics, and target-quality search — including a test that searches, encodes the full file, and asserts the sampled prediction held. They skip automatically when FFmpeg isn't on your PATH (and hardware tests skip without a usable GPU), so check the skip count before trusting a green run.

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
 Moist.Core.Tests/      Fast unit tests (88)
 Moist.IntegrationTests/ Real FFmpeg encodes verified with ffprobe (43)
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
- Default output and temp directories
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
| Tests | `Core.Tests/` — xUnit with 71 tests |

---

## CI/CD

- **CI** (`.github/workflows/ci.yml`): Builds Debug + Release and runs xUnit tests on Windows and Linux via self-hosted runners on every push/PR to `main`.
- **Release** (`.github/workflows/release.yml`): Triggered on `v*` tags. Produces self-contained Windows x64 (ZIP) and Linux x64 (tar.gz) builds. Creates GitHub releases with all artifacts.

---

## Troubleshooting

**FFmpeg not found** — Set the path in Settings, or put `ffmpeg` and `ffprobe` on your PATH. Or click "Install" in the Settings prerequisites panel.

**Build errors after pulling** — `cd src && dotnet restore && dotnet build`

**Encoding fails with "No processor found"** — The selected encoder's tool isn't installed or the path isn't configured in Settings.

**Audio-only output has wrong extension** — The extension auto-updates when you toggle Audio Only mode. If you set the output path manually, change the extension to match the codec (.opus, .m4a, .mp3, .flac).

**Theme not applying** — Click "Save Settings" after changing the theme. It applies on save.

**Two-pass encoding produces no output** — Check that the output directory is writable and has enough disk space. Two-pass creates temporary log files next to the output.

---

## Contributing

1. Fork it
2. Branch it: `git checkout -b feature/my-feature`
3. Build it: `dotnet build`
4. Test it: `dotnet test` (all 71 tests must pass)
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
