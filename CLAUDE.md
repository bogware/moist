# CLAUDE.md - moist

## Project Overview

**moist** is a cross-platform video encoder GUI. Built with C# / .NET 8 / Avalonia UI, it provides a professional-grade frontend for video encoding, audio encoding, filtering, and muxing. Inspired by MeGUI, modernized for cross-platform use.

## Build, Run & Test

```bash
cd src
dotnet restore
dotnet build
dotnet run --project Moist.App
dotnet test                              # runs 71 xUnit tests in Moist.Core.Tests
```

**Prerequisites**: .NET 8 SDK, FFmpeg/FFprobe on PATH (for encoding)

## Repository Structure

```
/
├── CLAUDE.md
├── README.md
├── .github/workflows/
│   ├── ci.yml                            # CI: build + test on Ubuntu/Windows
│   └── release.yml                       # Release: Windows/Linux self-contained builds
├── src/
│   ├── Moist.sln                         # Solution file (4 projects)
│   ├── Moist.Core/                       # Core library (no UI dependency)
│   │   ├── Jobs/                         # Job system (queue, workers, processors)
│   │   │   ├── Job.cs                    # Base job class
│   │   │   ├── VideoJob.cs              # Video encoding job
│   │   │   ├── AudioJob.cs              # Audio encoding job
│   │   │   ├── MuxJob.cs               # Muxing job
│   │   │   ├── JobQueue.cs             # Thread-safe queue with dependencies + per-job factories
│   │   │   ├── JobStatus.cs            # Job lifecycle states
│   │   │   ├── JobProgress.cs          # Progress reporting
│   │   │   ├── IJobProcessor.cs        # Processor interface (async)
│   │   │   ├── CommandLineProcessor.cs # Base for CLI tool processors
│   │   │   └── TwoPassProcessor.cs     # Two-pass encoding wrapper
│   │   ├── Encoders/
│   │   │   ├── EncoderRegistry.cs       # Central encoder registration (factory pattern)
│   │   │   ├── EncoderInfo.cs           # Encoder metadata
│   │   │   ├── Video/
│   │   │   │   ├── VideoEncoderSettings.cs  # Base class + enums
│   │   │   │   ├── X264Settings.cs
│   │   │   │   ├── X265Settings.cs
│   │   │   │   ├── SvtAv1Settings.cs
│   │   │   │   ├── FFmpegVideoProcessor.cs  # FFmpeg video encoding (with -tune support)
│   │   │   │   ├── X264CliProcessor.cs      # Standalone x264 CLI
│   │   │   │   └── X265CliProcessor.cs      # Standalone x265 CLI
│   │   │   └── Audio/
│   │   │       ├── AudioEncoderSettings.cs  # Base class + enums (BitrateMode, ChannelMode, Normalization, SampleFormat)
│   │   │       ├── OpusSettings.cs          # Opus with complexity
│   │   │       ├── AacSettings.cs           # AAC with profile
│   │   │       ├── Mp3Settings.cs           # LAME MP3 with VBR scale + channel mode
│   │   │       ├── FlacSettings.cs          # FLAC with compression level
│   │   │       ├── Ac3Settings.cs           # AC3 with dialogue norm + surround mode
│   │   │       ├── Eac3Settings.cs          # E-AC3 with dialogue norm + mixing level
│   │   │       ├── VorbisSettings.cs        # Vorbis with quality scale
│   │   │       └── FFmpegAudioProcessor.cs  # All codecs + normalization filters
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
│   │   │   └── EncodingProfile.cs       # Profiles + ProfileManager (7 built-in presets)
│   │   ├── Tools/
│   │   │   └── ToolDiscovery.cs         # Auto-discovery, version detection, download
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
│   │   │   └── AppServices.cs           # DI / service wiring / processor factory registration
│   │   ├── ViewModels/
│   │   │   ├── ViewModelBase.cs
│   │   │   ├── MainWindowViewModel.cs
│   │   │   ├── EncodeTabViewModel.cs    # Full audio settings + audio-only mode
│   │   │   ├── QueueTabViewModel.cs     # Auto-start queue support
│   │   │   ├── FilterTabViewModel.cs
│   │   │   ├── BatchTabViewModel.cs     # Batch encoding with per-file profiles
│   │   │   ├── PluginManagerViewModel.cs
│   │   │   └── SettingsTabViewModel.cs  # Tool discovery + prerequisites
│   │   └── Views/
│   │       ├── MainWindow.axaml / .cs
│   │       ├── EncodeTabView.axaml / .cs  # 8-row audio panel + SVT-AV1 preset
│   │       ├── QueueTabView.axaml / .cs
│   │       ├── FilterTabView.axaml / .cs
│   │       ├── BatchTabView.axaml / .cs
│   │       ├── PluginManagerView.axaml / .cs
│   │       └── SettingsTabView.axaml / .cs
│   └── Moist.Core.Tests/                # xUnit test project (71 tests)
│       ├── EncoderRegistryTests.cs
│       ├── JobModelTests.cs
│       ├── JobQueueTests.cs
│       ├── ProfileTests.cs
│       ├── SettingsTests.cs
│       ├── FilterEngineTests.cs
│       └── AudioProcessorTests.cs       # 28 tests for all codec arg building
```

## Architecture

### Four-Project Structure
- **Moist.Core** — No UI dependencies. Job system, encoders, media analysis, profiles, settings, tool discovery. Reusable across UI frameworks.
- **Moist.Filters** — Filter engine abstraction layer. Supports FFmpeg, VapourSynth, and AviSynth filter chains. Includes presets and plugin source management.
- **Moist.App** — Avalonia UI application. MVVM pattern with CommunityToolkit.Mvvm.
- **Moist.Core.Tests** — 71 xUnit tests covering jobs, encoders, profiles, settings, filter engines, and audio processor argument building.

### Key Design Patterns
- **Per-Job Processor Factories** — Each job gets a fresh processor instance via `RegisterProcessorFactory()`. Prevents shared mutable state (stopwatches, frame counters) between concurrent jobs.
- **Job Queue with Dependencies** — Jobs can depend on other jobs. Video+Audio encode, then mux.
- **CommandLineProcessor** — Base class for all CLI tool integrations. Async with progress parsing.
- **TwoPassProcessor** — Wraps video encoders for two-pass bitrate mode with snapshot/restore.
- **Encoder Registry** — Dynamic registration of encoders via factory pattern (`Func<IJobProcessor>`).
- **Filter Chain** — Engine-agnostic filter definitions translated to engine-specific scripts.
- **Profile System** — Saved encoding configurations with 7 built-in presets. Stores all video/audio settings including normalization, codec-specific options, and filter chains.
- **MVVM** — ViewModels drive UI via data binding. Views are pure AXAML.

### Job Pipeline (typical encode)
1. User configures encode settings (or loads a profile) in Encode tab
2. "Add to Queue" creates: VideoJob + AudioJob + MuxJob (with dependencies)
3. Queue processes jobs respecting dependencies and concurrency limits
4. Per-job processor factory creates fresh processor instance (no shared state)
5. CommandLineProcessor launches FFmpeg/x264/x265, parses progress from stdout/stderr
6. MuxJob combines encoded streams into final container
7. Temp files cleaned up only on success (preserved on failure for debugging)

### Audio-Only Pipeline
1. When AudioOnly is enabled, only AudioJob is created (no VideoJob, no MuxJob)
2. Audio job outputs directly to final file with codec-appropriate extension
3. Supports all audio codecs, normalization, and codec-specific settings

### Filter Engines
| Engine | Platform | Script Type |
|--------|----------|-------------|
| FFmpeg | All | `-filter_complex` argument |
| VapourSynth | All | `.vpy` Python script |
| AviSynth+ | Windows | `.avs` script |

### Supported Encoders (via FFmpeg)
**Video**: x264, x265, SVT-AV1, VP9, FFV1 (with -tune support for x264/x265/VP9)
**Audio**: Opus, AAC, MP3, FLAC, AC3, E-AC3, Vorbis, Passthrough (copy)
**Audio Features**: EBU R128/Peak/RMS normalization, sample format, channel mode, codec-specific options
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
- Processor classes should NOT be sealed (allows test subclassing for argument verification)

## Key Files for Common Tasks

| Task | Key Files |
|------|-----------|
| Add a video encoder | `Moist.Core/Encoders/Video/` — add Settings class + register in `AppServices.cs` + handle in `EncodeTabViewModel.EnqueueFile` |
| Add an audio encoder | `Moist.Core/Encoders/Audio/` — add Settings class + register in `AppServices.cs` + add codec args in `FFmpegAudioProcessor.GetCodecSpecificArgs()` + handle in `EncodeTabViewModel.CreateAudioSettings()` |
| Add audio encoder test | `Moist.Core.Tests/AudioProcessorTests.cs` — subclass `FFmpegAudioProcessor` as `TestableAudioProcessor` to test `BuildArguments()` |
| Add a filter | `Moist.Filters/Engines/` — add to appropriate engine's `GetAvailableFilters()` |
| Add a new filter engine | Implement `IFilterEngine`, register in `AppServices.RegisterDefaultFilters()` |
| Add an encoding profile | `Moist.Core/Profiles/EncodingProfile.cs` — add to `GetBuiltInProfiles()` |
| Modify the UI | `Moist.App/Views/` (AXAML) + `Moist.App/ViewModels/` |
| Change settings | `Moist.Core/Settings/MoistSettings.cs` + `SettingsTabViewModel.cs` |
| Add a new muxer | `Moist.Core/Muxers/` — implement `CommandLineProcessor` |
| Add tests | `Moist.Core.Tests/` — xUnit test classes |
| Modify theme | `Moist.App/Themes/MoistDark.axaml` or `MoistTheme.axaml` |

## Important Implementation Details

### Processor Factories (NOT Singletons)
Processors are registered as factories via `JobQueue.RegisterProcessorFactory()`, not as shared singletons. This is critical: processors contain mutable state (Stopwatch, frame counters) that would cause race conditions if shared between concurrent jobs. `AppServices.RegisterDefaultProcessors()` uses `RegisterProcessorFactory(() => Encoders.CreateProcessor(encoderId))`.

### Audio-Only Mode
When `AudioOnly` is true in `EncodeTabViewModel`:
- `EnqueueFile()` skips VideoJob creation entirely
- AudioJob outputs directly to the final output file (no temp file, no MuxJob)
- Output file extension auto-updates to match the selected codec (.opus, .m4a, .mp3, etc.)
- Returns the AudioJob ID instead of MuxJob ID

### Audio Normalization Filters
Generated in `FFmpegAudioProcessor.BuildNormalizationFilter()`:
- **EBU R128**: `loudnorm=I={target}:TP={peak}:LRA=11:print_format=summary`
- **Peak**: `acompressor=threshold={peak}dB:ratio=20:attack=0.1:release=50,volume={target}dB`
- **RMS**: `dynaudnorm=f=150:g=15:p={10^(target/20)}:m=10` (target converted from dB to linear)

### Temp File Cleanup
Temp files (intermediate video/audio encodes) are only cleaned up when the job completes successfully. On failure, they are preserved for debugging and retry.

## CI/CD

- **CI** (`.github/workflows/ci.yml`): Runs on push/PR to `main`. Builds Debug + Release and runs xUnit tests on Windows and Linux using self-hosted runners (runner group `Default`, labels `windows`/`linux`). macOS job is commented out.
- **Release** (`.github/workflows/release.yml`): Triggered on `v*` tags. Produces self-contained Windows x64 (ZIP) and Linux x64 (tar.gz) builds. Creates GitHub releases with all artifacts.

## Data Persistence

- **Settings**: `%AppData%/moist/settings.json` (Windows) / `~/.config/moist/settings.json` (Linux/macOS)
- **Profiles**: `%AppData%/moist/profiles/*.json` — individual JSON files per profile
- **Tools**: `%AppData%/moist/tools/` — auto-downloaded tool binaries
- Serialization uses `System.Text.Json` with `WriteIndented = true` and `JsonStringEnumConverter`
