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

### Subtitle Support

- **Copy all subtitles** from source with proper default/forced disposition
- **External subtitle import** — Add .srt/.ass files alongside the encode
- **Per-track metadata** — Language and title preserved through muxing

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
dotnet run --project Moist.App
```

### Release Build

```bash
cd src
dotnet publish Moist.App -c Release -o ../dist
```

### Run Tests

```bash
cd src
dotnet test
```

71 unit tests covering job models, queue behavior, encoder registry, profiles, settings, filter engines, and audio processor argument building.

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
    Muxers/             Container muxing (FFmpeg, deduplicated input mapping)
    Media/              FFprobe analysis, subtitle/audio stream parsing
    Settings/           JSON-based app settings
    Profiles/           Encoding profile save/load (7 built-in + user profiles)
    Tools/              Tool discovery, version detection, auto-download
    Plugins/            Plugin interface
 Moist.Filters/         Filter engine abstraction layer
    Engines/            FFmpeg, VapourSynth, AviSynth implementations
    FilterChain         Ordered, engine-agnostic filter chain
    FilterPreset        Built-in + user presets with JSON persistence
    PluginSource        Plugin source management + marketplace catalog
 Moist.App/             Avalonia UI application
    Services/           AppServices — central DI/service wiring
    ViewModels/         MVVM ViewModels (CommunityToolkit.Mvvm)
    Views/              AXAML views with compiled bindings
    Themes/             Skinnable theme overlays (MoistDark, etc.)
 Moist.Core.Tests/      xUnit test suite (71 tests)
```

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
2. Register in `AppServices.cs` via `RegisterVideoEncoder()`
3. Handle in `EncodeTabViewModel.EnqueueFile` switch

### Adding an Audio Encoder

1. Create settings class extending `AudioEncoderSettings` in `Moist.Core/Encoders/Audio/`
2. Register in `AppServices.cs` via `RegisterAudioEncoder()`
3. Add codec-specific args in `FFmpegAudioProcessor.GetCodecSpecificArgs()`
4. Handle in `EncodeTabViewModel.CreateAudioSettings()` switch

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
