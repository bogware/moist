# moist

**Modern Open-source Intelligent Streaming Transcoder**

> *"There's two kinds of people in this world: those who encode their video right, and those who wish they had."*

A cross-platform video encoder GUI. Built with C# / .NET 8 / [Avalonia UI](https://avaloniaui.net/), moist provides a powerful yet easy-to-use frontend for video encoding, audio encoding, filtering, and muxing.

It won't judge your source material. It'll just make it better.

---

## Features

### Encoding

The heart of the operation. No shortcuts, no compromises.

- **Video Encoders**: x264, x265, SVT-AV1, VP9, FFV1 — via FFmpeg or standalone CLI
- **Audio Encoders**: Opus, AAC, MP3, FLAC, AC3, E-AC3, Vorbis
- **Encoding Modes**: CRF, single-pass bitrate, two-pass bitrate
- **Presets & Tuning**: Full preset/tune control for x264/x265/SVT-AV1
- **Custom Parameters**: Pass arbitrary encoder arguments — for those who know what they're doing
- **Audio Track Selection**: Pick your stream, don't settle for whatever FFmpeg grabs first
- **Subtitle Copy**: Copies all subtitle streams from the source with proper default/forced disposition

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
- **Real-time progress** — FPS, ETA, percentage for video and audio
- **Per-job cancellation** — Cancel one job without burning down the whole queue
- **Two-pass support** — Automatic two-pass handling for FFmpeg and standalone CLI encoders

### Batch Processing

Drop your files. Walk away. Come back to finished encodes.

- **Drag-and-drop** — Drop a whole folder's worth of files at once
- **Per-item status tracking** — Pending → Queued → Encoding → Completed
- **Flexible output paths** — Source directory or custom output with configurable suffix

### Plugin Marketplace

- **Local + online sources** — Add local plugin directories or browse online repositories
- **20+ curated plugins** — VapourSynth, AviSynth, FFmpeg ecosystem
- **Enable/disable sources** — Toggle without removing. Civilized.

### Profiles

- **Save/load encoding profiles** — Video encoder, audio encoder, container, all settings
- **One-click apply** — Stop re-entering the same CRF value for the hundredth time

### Theming

- **moist Dark** — The house skin. Blue-gray professional palette, low-contrast, easy on the eyes during long encode sessions
- **Fluent Dark** — Avalonia's built-in dark theme, if you prefer the stock look
- **Live switching** — Change themes in Settings, see it immediately. No restart required.

### Container Formats

MKV, MP4, WebM, AVI, TS, MOV

---

## Quick Start

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- [FFmpeg](https://ffmpeg.org/download.html) and FFprobe on your PATH
- Optional: [x264](https://www.videolan.org/developers/x264.html), [x265](https://www.videolan.org/developers/x265.html) for standalone CLI encoding
- Optional: [VapourSynth](https://www.vapoursynth.com/) for VapourSynth filters
- Optional: [MKVToolNix](https://mkvtoolnix.download/) for MKVMerge muxing

### Build & Run

```bash
git clone https://github.com/bogware/moist.git
cd moist/src
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

If FFmpeg isn't on your PATH, set the path in the **Settings** tab after launching. moist won't hold it against you.

---

## Architecture

> *"Out here, a man builds things to last — or he doesn't build 'em at all."*

### Three-Project Structure

```
src/
├── Moist.Core/        # Core library — no UI dependency
│   ├── Jobs/          # Job queue, workers, processors, two-pass
│   ├── Encoders/      # Video + Audio encoder abstractions & settings
│   ├── Muxers/        # Container muxing (FFmpeg, deduplicated input mapping)
│   ├── Media/         # FFprobe analysis, subtitle/audio stream parsing
│   ├── Settings/      # JSON-based app settings
│   ├── Profiles/      # Encoding profile save/load
│   └── Plugins/       # Plugin interface
├── Moist.Filters/     # Filter engine abstraction layer
│   ├── Engines/       # FFmpeg, VapourSynth, AviSynth implementations
│   ├── FilterChain    # Ordered, engine-agnostic filter chain
│   ├── FilterPreset   # Built-in + user presets with JSON persistence
│   └── PluginSource   # Plugin source management + marketplace catalog
├── Moist.App/         # Avalonia UI application
│   ├── Services/      # AppServices — central DI/service wiring
│   ├── ViewModels/    # MVVM ViewModels (CommunityToolkit.Mvvm)
│   ├── Views/         # AXAML views
│   └── Themes/        # Skinnable theme overlays (MoistDark, etc.)
└── Moist.Core.Tests/  # xUnit test suite
```

### Key Design Patterns

| Pattern | Description |
|---------|-------------|
| **MVVM** | ViewModels drive UI via data binding. Views are pure AXAML. |
| **Job Queue with Dependencies** | Jobs declare dependencies. Video+Audio run in parallel, mux waits for both. |
| **CommandLineProcessor** | Base class for all CLI tool integrations. Async with progress parsing. |
| **Encoder Registry** | Dynamic registration of encoders via factory pattern. |
| **Filter Chain** | Engine-agnostic filter definitions translated to engine-specific scripts. |
| **Theme Overlay** | Additive style system — skins layer on top of FluentTheme without replacing it. |

### Encoding Pipeline

```
User configures settings
        │
        ▼
  [Add to Queue]
        │
        ├── VideoJob (video encode → temp .mkv)
        │     └── with FilterScript from filter chain
        ├── AudioJob (audio encode → temp .mka)
        │     └── selected stream, duration-based progress
        └── MuxJob (depends on video + audio)
              └── video + audio + subtitles → final .mkv/.mp4/.webm
```

---

## Supported Encoders

### Video

| Encoder | ID | Notes |
|---------|----|-------|
| x264 via FFmpeg | `ffmpeg-x264` | H.264 — the reliable deputy |
| x265 via FFmpeg | `ffmpeg-x265` | H.265 — twice the efficiency, twice the patience |
| SVT-AV1 via FFmpeg | `ffmpeg-svtav1` | AV1 — the new sheriff in town |
| VP9 via FFmpeg | `ffmpeg-vp9` | WebM's finest |
| FFV1 Lossless | `ffmpeg-ffv1` | For the archivists who don't trust lossy |
| x264 Standalone CLI | `cli-x264` | Direct x264 via FFmpeg pipe |
| x265 Standalone CLI | `cli-x265` | Direct x265 via FFmpeg pipe |

### Audio

| Encoder | ID | Notes |
|---------|----|-------|
| Opus | `ffmpeg-audio-opus` | Best general-purpose lossy codec. Just use this. |
| AAC | `ffmpeg-audio-aac` | When compatibility matters more than quality |
| MP3 (LAME) | `ffmpeg-audio-mp3` | For the nostalgic |
| FLAC | `ffmpeg-audio-flac` | Lossless — because bits matter |
| AC3 (Dolby) | `ffmpeg-audio-ac3` | Dolby Digital |
| E-AC3 (Dolby+) | `ffmpeg-audio-eac3` | Enhanced Dolby Digital Plus |
| Vorbis | `ffmpeg-audio-vorbis` | For WebM containers |

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
- Anime Upscale 720p→1080p — Lanczos + gentle sharpen
- Anime Deband + Clean — Deband + light temporal denoise

### Restoration
- VHS Capture Restoration — Full pipeline: deinterlace → denoise → color fix → sharpen
- Archival Preservation — Minimal processing for digitization

### Color / Creative / Speed / Web
Color grading, vintage looks, slow motion, speed-ups, web optimization, social media crops — 30+ presets in total. Or save your own.

---

## Configuration

Settings are stored as JSON:
- **Linux/macOS**: `~/.config/moist/settings.json`
- **Windows**: `%APPDATA%/moist/settings.json`

### Tool Paths
FFmpeg, FFprobe, MKVMerge, x264, x265, VapourSynth — configurable in the Settings tab.

### Options
- Max concurrent jobs (1–32)
- Auto-start queue on enqueue
- Delete temp files after completion
- Default output and temp directories
- Theme selection (moist Dark / Fluent Dark)

---

## Developer Guide

### Code Conventions

- **C# 12** / .NET 8, nullable enabled, implicit usings
- **MVVM** via `CommunityToolkit.Mvvm` (`[ObservableProperty]`, `[RelayCommand]`)
- **Avalonia AXAML** with `x:DataType` (compiled bindings)
- PascalCase for public members, `_camelCase` for private fields
- Async-first: `Task` + `CancellationToken` everywhere
- JSON settings with `JsonStringEnumConverter` for human-readable enums

### Adding a Video Encoder

1. Create settings class in `Moist.Core/Encoders/Video/`
2. Register in `AppServices.cs` via `RegisterVideoEncoder()`
3. Handle in `EncodeTabViewModel.EnqueueFile` switch

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
| Audio encoder | `Core/Encoders/Audio/` + `AppServices.cs` |
| Filter | `Filters/Engines/` + `FilterPreset.cs` |
| UI tab | `App/Views/*.axaml` + `ViewModels/*.cs` |
| Settings | `Core/Settings/MoistSettings.cs` + `SettingsTabViewModel.cs` |
| Theme | `App/Themes/` + `App.axaml` + `App.axaml.cs` |

---

## Troubleshooting

**FFmpeg not found** — Set the path in Settings, or put `ffmpeg` and `ffprobe` on your PATH.

**Build errors after pulling** — `cd src && dotnet restore && dotnet build`

**Encoding fails with "No processor found"** — The selected encoder's tool isn't installed or the path isn't configured.

**Theme not applying** — Click "Save Settings" after changing the theme. It applies on save, not on selection.

---

## Contributing

1. Fork it
2. Branch it: `git checkout -b feature/my-feature`
3. Build it: `dotnet build`
4. Test it: `dotnet test`
5. Commit it with a clear message
6. Open a Pull Request

---

## License

See [LICENSE](LICENSE) for details.

---

> *"Some folks encode with fear. Some encode with reckless abandon. The smart ones use moist."*
>
> *— Inspired by the long silences and hard truths of Gunsmoke*

*moist — because your encodes should be fresh.*
