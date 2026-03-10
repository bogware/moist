# CLAUDE.md - moist

## Project Overview

**moist** is a cross-platform video encoder GUI. Built with C# / .NET 8 / Avalonia UI, it provides a powerful yet easy-to-use frontend for video encoding, audio encoding, filtering, and muxing. Inspired by MeGUI, modernized for cross-platform use.

## Build, Run & Test

```bash
cd src
dotnet restore
dotnet build
dotnet run --project Moist.App
dotnet test                              # runs xUnit tests in Moist.Core.Tests
```

**Prerequisites**: .NET 8 SDK, FFmpeg/FFprobe on PATH (for encoding)

## Repository Structure

```
/
├── CLAUDE.md
├── .github/workflows/
│   ├── ci.yml                            # CI: build + test on Ubuntu/Windows/macOS
│   └── release.yml                       # Release: Windows self-contained builds
├── src/
│   ├── Moist.sln                         # Solution file (4 projects)
│   ├── Moist.Core/                       # Core library (no UI dependency)
│   │   ├── Jobs/                         # Job system (queue, workers, processors)
│   │   │   ├── Job.cs                    # Base job class
│   │   │   ├── VideoJob.cs              # Video encoding job
│   │   │   ├── AudioJob.cs              # Audio encoding job
│   │   │   ├── MuxJob.cs               # Muxing job
│   │   │   ├── JobQueue.cs             # Thread-safe job queue with dependencies
│   │   │   ├── JobStatus.cs            # Job lifecycle states
│   │   │   ├── JobProgress.cs          # Progress reporting
│   │   │   ├── IJobProcessor.cs        # Processor interface (async)
│   │   │   ├── CommandLineProcessor.cs # Base for CLI tool processors
│   │   │   └── TwoPassProcessor.cs     # Two-pass encoding wrapper
│   │   ├── Encoders/
│   │   │   ├── EncoderRegistry.cs       # Central encoder registration (factory pattern)
│   │   │   ├── EncoderInfo.cs           # Encoder metadata
│   │   │   ├── Video/
│   │   │   │   ├── VideoEncoderSettings.cs  # Base class + enums (VideoEncodingMode, VideoPreset, VideoTune)
│   │   │   │   ├── X264Settings.cs
│   │   │   │   ├── X265Settings.cs
│   │   │   │   ├── SvtAv1Settings.cs
│   │   │   │   ├── FFmpegVideoProcessor.cs  # FFmpeg-based video encoding
│   │   │   │   ├── X264CliProcessor.cs      # Standalone x264 CLI
│   │   │   │   └── X265CliProcessor.cs      # Standalone x265 CLI
│   │   │   └── Audio/
│   │   │       ├── AudioEncoderSettings.cs  # Base class + enums (AudioBitrateMode)
│   │   │       ├── OpusSettings.cs
│   │   │       ├── AacSettings.cs
│   │   │       ├── FlacSettings.cs
│   │   │       └── FFmpegAudioProcessor.cs
│   │   ├── Muxers/
│   │   │   ├── ContainerFormat.cs       # Enum: Mkv, Mp4, WebM, Avi, Ts, Mov
│   │   │   └── FFmpegMuxProcessor.cs
│   │   ├── Media/
│   │   │   ├── MediaInfo.cs             # Media file info models
│   │   │   ├── FFprobeMediaAnalyzer.cs  # FFprobe integration
│   │   │   └── VideoPreviewService.cs   # Frame preview extraction
│   │   ├── Settings/
│   │   │   └── MoistSettings.cs         # App settings (JSON persistence)
│   │   ├── Profiles/
│   │   │   └── EncodingProfile.cs       # Encoding profiles + ProfileManager (7 built-in presets)
│   │   └── Plugins/
│   │       └── IPlugin.cs
│   ├── Moist.Filters/                    # Filter engine library
│   │   ├── FilterDefinition.cs          # Engine-agnostic filter model
│   │   ├── FilterChain.cs              # Ordered filter chain
│   │   ├── IFilterEngine.cs            # Filter engine interface
│   │   ├── FilterRegistry.cs           # Engine registry
│   │   ├── FilterPreset.cs             # Saved filter chain presets
│   │   ├── PluginSource.cs             # Plugin source management (local + online)
│   │   └── Engines/
│   │       ├── FFmpegFilterEngine.cs        # FFmpeg filters (cross-platform)
│   │       ├── VapourSynthFilterEngine.cs   # VapourSynth (cross-platform)
│   │       └── AviSynthFilterEngine.cs      # AviSynth+ (Windows-only)
│   ├── Moist.App/                        # Avalonia UI application
│   │   ├── App.axaml / App.axaml.cs
│   │   ├── Program.cs
│   │   ├── ViewLocator.cs
│   │   ├── Themes/
│   │   │   ├── MoistTheme.axaml         # Base theme
│   │   │   └── MoistDark.axaml          # Dark theme variant
│   │   ├── Services/
│   │   │   └── AppServices.cs           # DI / service wiring
│   │   ├── ViewModels/
│   │   │   ├── ViewModelBase.cs
│   │   │   ├── MainWindowViewModel.cs
│   │   │   ├── EncodeTabViewModel.cs
│   │   │   ├── QueueTabViewModel.cs
│   │   │   ├── FilterTabViewModel.cs
│   │   │   ├── BatchTabViewModel.cs         # Batch encoding support
│   │   │   ├── PluginManagerViewModel.cs    # Plugin/filter management
│   │   │   └── SettingsTabViewModel.cs
│   │   └── Views/
│   │       ├── MainWindow.axaml / .cs
│   │       ├── EncodeTabView.axaml / .cs
│   │       ├── QueueTabView.axaml / .cs
│   │       ├── FilterTabView.axaml / .cs
│   │       ├── BatchTabView.axaml / .cs
│   │       ├── PluginManagerView.axaml / .cs
│   │       └── SettingsTabView.axaml / .cs
│   └── Moist.Core.Tests/                # xUnit test project
│       ├── EncoderRegistryTests.cs
│       ├── JobModelTests.cs
│       ├── JobQueueTests.cs
│       ├── ProfileTests.cs
│       └── SettingsTests.cs
```

## Architecture

### Four-Project Structure
- **Moist.Core** — No UI dependencies. Job system, encoders, media analysis, profiles, settings. Reusable across UI frameworks.
- **Moist.Filters** — Filter engine abstraction layer. Supports FFmpeg, VapourSynth, and AviSynth filter chains. Includes presets and plugin source management.
- **Moist.App** — Avalonia UI application. MVVM pattern with CommunityToolkit.Mvvm.
- **Moist.Core.Tests** — xUnit tests covering core library (jobs, encoders, profiles, settings).

### Key Design Patterns
- **Job Queue with Dependencies** — Jobs can depend on other jobs. Video+Audio encode, then mux.
- **CommandLineProcessor** — Base class for all CLI tool integrations. Async with progress parsing.
- **TwoPassProcessor** — Wraps video encoders for two-pass bitrate mode.
- **Encoder Registry** — Dynamic registration of encoders via factory pattern (`Func<IJobProcessor>`).
- **Filter Chain** — Engine-agnostic filter definitions translated to engine-specific scripts.
- **Profile System** — Saved encoding configurations with 7 built-in presets (Web H.265, H.264, Archive Lossless, YouTube, AV1, Anime, Fast Preview).
- **MVVM** — ViewModels drive UI via data binding. Views are pure AXAML.

### Job Pipeline (typical encode)
1. User configures encode settings (or loads a profile) in Encode tab
2. "Add to Queue" creates: VideoJob + AudioJob + MuxJob (with dependencies)
3. Queue processes jobs respecting dependencies and concurrency limits
4. CommandLineProcessor launches FFmpeg/x264/x265, parses progress from stdout/stderr
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

### Key Dependencies
| Package | Version | Purpose |
|---------|---------|---------|
| Avalonia | 11.3.12 | Cross-platform UI framework |
| Avalonia.Themes.Fluent | 11.3.12 | Fluent design theme |
| Avalonia.Controls.DataGrid | 11.3.12 | Queue/batch data grids |
| CommunityToolkit.Mvvm | 8.4.0 | MVVM source generators |
| Microsoft.Extensions.Logging | 8.0.x | Logging abstraction |
| xunit | 2.5.3 | Unit testing |

## Code Conventions

- **C# 12** with .NET 8, nullable enabled, implicit usings
- **MVVM** via CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`)
- **Avalonia AXAML** for views — dark Fluent theme with custom `MoistDark.axaml`
- PascalCase for public members, `_camelCase` for private fields
- `[ObservableProperty]` on private `_camelCase` fields auto-generates public PascalCase properties
- File-scoped namespaces (`namespace Moist.Core.Jobs;`)
- Settings and profiles persisted as JSON via `System.Text.Json` (with `JsonStringEnumConverter`)
- Async-first: all job processing uses `Task` + `CancellationToken`
- Constructor injection for services in ViewModels
- Namespaces follow folder structure: `Moist.Core.Jobs`, `Moist.App.ViewModels`, `Moist.Filters.Engines`

## Key Files for Common Tasks

| Task | Key Files |
|------|-----------|
| Add a video encoder | `Moist.Core/Encoders/Video/` — add Settings class + register in `AppServices.cs` |
| Add an audio encoder | `Moist.Core/Encoders/Audio/` — add Settings class + register in `AppServices.cs` |
| Add a filter | `Moist.Filters/Engines/` — add to appropriate engine's `GetAvailableFilters()` |
| Add a new filter engine | Implement `IFilterEngine`, register in `AppServices.RegisterDefaultFilters()` |
| Add an encoding profile | `Moist.Core/Profiles/EncodingProfile.cs` — add to `GetBuiltInProfiles()` |
| Modify the UI | `Moist.App/Views/` (AXAML) + `Moist.App/ViewModels/` |
| Change settings | `Moist.Core/Settings/MoistSettings.cs` + `SettingsTabViewModel.cs` |
| Add a new muxer | `Moist.Core/Muxers/` — implement `CommandLineProcessor` |
| Add tests | `Moist.Core.Tests/` — xUnit test classes |
| Modify theme | `Moist.App/Themes/MoistDark.axaml` or `MoistTheme.axaml` |

## CI/CD

- **CI** (`.github/workflows/ci.yml`): Runs on push/PR to `main`. Builds Debug + Release and runs xUnit tests on Windows and Linux using self-hosted runners (runner group `Default`, labels `windows`/`linux`). macOS job is commented out (uses `macos-latest` GitHub runner when enabled).
- **Release** (`.github/workflows/release.yml`): Triggered on `v*` tags. Produces self-contained and framework-dependent Windows x64 builds (ZIP) and self-contained Linux x64 builds (tar.gz) using self-hosted runners. macOS builds are commented out. Creates GitHub releases with all artifacts.

## Data Persistence

- **Settings**: `%AppData%/moist/settings.json` (Windows) / `~/.config/moist/settings.json` (Linux/macOS)
- **Profiles**: `%AppData%/moist/profiles/*.json` — individual JSON files per profile
- Serialization uses `System.Text.Json` with `WriteIndented = true` and `JsonStringEnumConverter`
