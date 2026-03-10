# CLAUDE.md - moist

## Project Overview

**moist** is a cross-platform video encoder GUI. Built with C# / .NET 8 / Avalonia UI, it provides a powerful yet easy-to-use frontend for video encoding, audio encoding, filtering, and muxing.

The repository contains two codebases:
- **`src/`** — The new **moist** application (active development)

## Build & Run

```bash
cd src
dotnet build
dotnet run --project Moist.App
```

**Prerequisites**: .NET 8 SDK, FFmpeg/FFprobe on PATH (for encoding)

## Repository Structure

```
/
├── CLAUDE.md
├── src/                              # moist — new cross-platform app
│   ├── Moist.sln                     # Solution file
│   ├── .gitignore
│   ├── Moist.Core/                   # Core library (no UI dependency)
│   │   ├── Jobs/                     # Job system (queue, workers, processors)
│   │   │   ├── Job.cs                # Base job class
│   │   │   ├── VideoJob.cs           # Video encoding job
│   │   │   ├── AudioJob.cs           # Audio encoding job
│   │   │   ├── MuxJob.cs             # Muxing job
│   │   │   ├── JobQueue.cs           # Thread-safe job queue with dependencies
│   │   │   ├── JobStatus.cs          # Job lifecycle states
│   │   │   ├── JobProgress.cs        # Progress reporting
│   │   │   ├── IJobProcessor.cs      # Processor interface (async)
│   │   │   └── CommandLineProcessor.cs # Base for CLI tool processors
│   │   ├── Encoders/                 # Encoder abstractions
│   │   │   ├── EncoderRegistry.cs    # Central encoder registration
│   │   │   ├── EncoderInfo.cs        # Encoder metadata
│   │   │   ├── Video/                # Video encoder settings
│   │   │   │   ├── VideoEncoderSettings.cs
│   │   │   │   ├── X264Settings.cs
│   │   │   │   ├── X265Settings.cs
│   │   │   │   ├── SvtAv1Settings.cs
│   │   │   │   └── FFmpegVideoProcessor.cs
│   │   │   └── Audio/                # Audio encoder settings
│   │   │       ├── AudioEncoderSettings.cs
│   │   │       ├── OpusSettings.cs
│   │   │       ├── AacSettings.cs
│   │   │       ├── FlacSettings.cs
│   │   │       └── FFmpegAudioProcessor.cs
│   │   ├── Muxers/                   # Container muxing
│   │   │   ├── ContainerFormat.cs
│   │   │   └── FFmpegMuxProcessor.cs
│   │   ├── Media/                    # Media analysis
│   │   │   ├── MediaInfo.cs          # Media file info models
│   │   │   └── FFprobeMediaAnalyzer.cs
│   │   ├── Settings/                 # App settings (JSON persistence)
│   │   │   └── MoistSettings.cs
│   │   └── Plugins/                  # Plugin system interface
│   │       └── IPlugin.cs
│   ├── Moist.Filters/                # Filter engine library
│   │   ├── FilterDefinition.cs       # Engine-agnostic filter model
│   │   ├── FilterChain.cs            # Ordered filter chain
│   │   ├── IFilterEngine.cs          # Filter engine interface
│   │   ├── FilterRegistry.cs         # Engine registry
│   │   └── Engines/
│   │       ├── FFmpegFilterEngine.cs     # FFmpeg filters (cross-platform)
│   │       ├── VapourSynthFilterEngine.cs # VapourSynth (cross-platform)
│   │       └── AviSynthFilterEngine.cs   # AviSynth+ (Windows-only)
│   └── Moist.App/                    # Avalonia UI application
│       ├── App.axaml / App.axaml.cs
│       ├── Program.cs
│       ├── ViewLocator.cs
│       ├── Services/
│       │   └── AppServices.cs        # DI / service wiring
│       ├── ViewModels/
│       │   ├── ViewModelBase.cs
│       │   ├── MainWindowViewModel.cs
│       │   ├── EncodeTabViewModel.cs
│       │   ├── QueueTabViewModel.cs
│       │   ├── FilterTabViewModel.cs
│       │   └── SettingsTabViewModel.cs
│       └── Views/
│           ├── MainWindow.axaml / .cs
│           ├── EncodeTabView.axaml / .cs
│           ├── QueueTabView.axaml / .cs
│           ├── FilterTabView.axaml / .cs
│           └── SettingsTabView.axaml / .cs
```

## Architecture

### Three-Project Structure
- **Moist.Core** — No UI dependencies. Job system, encoders, media analysis, settings. Reusable across UI frameworks.
- **Moist.Filters** — Filter engine abstraction layer. Supports FFmpeg, VapourSynth, and AviSynth filter chains.
- **Moist.App** — Avalonia UI application. MVVM pattern with CommunityToolkit.Mvvm.

### Key Design Patterns (inherited from MeGUI, modernized)
- **Job Queue with Dependencies** — Jobs can depend on other jobs. Video+Audio encode, then mux.
- **CommandLineProcessor** — Base class for all CLI tool integrations. Async with progress parsing.
- **Encoder Registry** — Dynamic registration of encoders via factory pattern.
- **Filter Chain** — Engine-agnostic filter definitions translated to engine-specific scripts.
- **MVVM** — ViewModels drive UI via data binding. Views are pure AXAML.

### Job Pipeline (typical encode)
1. User configures encode settings in Encode tab
2. "Add to Queue" creates: VideoJob + AudioJob + MuxJob (with dependencies)
3. Queue processes jobs respecting dependencies and concurrency limits
4. CommandLineProcessor launches FFmpeg, parses progress from stdout/stderr
5. MuxJob combines encoded streams into final container

### Filter Engines
| Engine | Platform | Script Type |
|--------|----------|-------------|
| FFmpeg | All | `-filter_complex` argument |
| VapourSynth | All | `.vpy` Python script |
| AviSynth+ | Windows | `.avs` script |

### Supported Encoders (via FFmpeg)
**Video**: x264, x265, SVT-AV1, VP9, FFV1
**Audio**: Opus, AAC, MP3, FLAC, AC3, E-AC3, Vorbis
**Muxers**: MKV, MP4, WebM, AVI, TS, MOV

## Code Conventions

- **C# 12** with .NET 8, nullable enabled, implicit usings
- **MVVM** via CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`)
- **Avalonia AXAML** for views — dark Fluent theme
- PascalCase for public members, `_camelCase` for private fields
- Settings persisted as JSON (not XML like legacy MeGUI)
- Async-first: all job processing uses `Task` + `CancellationToken`

## Key Files for Common Tasks

| Task | Key Files |
|------|-----------|
| Add a video encoder | `Moist.Core/Encoders/Video/` — add Settings class + register in `AppServices.cs` |
| Add an audio encoder | `Moist.Core/Encoders/Audio/` — add Settings class + register in `AppServices.cs` |
| Add a filter | `Moist.Filters/Engines/` — add to appropriate engine's filter list |
| Add a new filter engine | Implement `IFilterEngine`, register in `AppServices.RegisterDefaultFilters()` |
| Modify the UI | `Moist.App/Views/` (AXAML) + `Moist.App/ViewModels/` |
| Change settings | `Moist.Core/Settings/MoistSettings.cs` + `SettingsTabViewModel.cs` |
| Add a new muxer | `Moist.Core/Muxers/` — implement `CommandLineProcessor` |

## Legacy Codebase (Reference)

The `legacy/` directory contains the original MeGUI v6666 — a Windows-only WinForms application on .NET Framework 4.8.1. It serves as the reference implementation for moist's design.

**Key legacy patterns carried forward to moist:**
- Job queue with worker pool and dependency resolution
- CommandLine processor base class for CLI tools
- Encoder settings as typed classes
- Plugin/package registration system
- Filter script generation (AviSynth)

**Key improvements in moist over legacy:**
- Cross-platform (Avalonia UI vs WinForms)
- Modern .NET 8 (vs .NET Framework 4.8.1)
- Async-first job processing (vs synchronous threads)
- JSON settings (vs XML serialization)
- MVVM architecture (vs mediator/singleton MainForm)
- Triple filter engine (FFmpeg + VapourSynth + AviSynth vs AviSynth-only)
- Built-in filter chain GUI (vs manual script editing)
