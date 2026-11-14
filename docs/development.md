# moist — development guide

[← back to the README](../README.md) · [Features](features.md) · [Command line](cli.md) · [FAQ](faq.md) · [Development](development.md)

For contributors: building, testing, how the pieces fit, and how to extend them. The deepest design notes — the *why* behind every decision — live in [`CLAUDE.md`](../CLAUDE.md).

## Build from source

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

**380+ unit tests** covering job models, queue behavior, encoder registry, profiles, settings, filter engines (including the restoration filters and profile-carried filter chains), container/extension mapping, audio processor argument building, hardware encoder plumbing, quality metric parsing, search arithmetic, chunk planning, the per-scene complexity model, and CLI parsing — none of which launch an external process.

**170+ integration tests** that run real encodes through the full pipeline and verify the output with ffprobe: every video encoder, every audio codec, every container, 10-bit and HDR sources, silent sources, multi-track audio, chapters and attachments, chroma and subtitle handling, filter chains, cancellation, concurrency, awkward filenames, temp-file cleanup, all four quality metrics, target-quality search, chunked encoding, per-scene optimisation, and queue persistence — including a test that searches then encodes the full file and asserts the sampled prediction held, one that deliberately truncates a chunk to prove the frame-count verification catches it, one that checks per-scene beats a fixed CRF chosen for the hardest scene, and one that measures the picture to prove burned-in subtitles were actually rendered rather than merely not erroring. They skip automatically when FFmpeg isn't on your PATH (and hardware tests skip without a usable GPU), so check the skip count before trusting a green run.

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
    Profiles/           Encoding profile save/load (16 built-in + user profiles)
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
 Moist.Core.Tests/      Fast unit tests — never launch a process
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

- **CI** (`.github/workflows/ci.yml`): builds Debug + Release and runs the full test suite on Windows and Linux for every push and pull request to `main`. Pull requests from forks run on GitHub-hosted runners; only trusted branches use the project's self-hosted ones.
- **Release** (`.github/workflows/release.yml`): started by hand — `gh workflow run release.yml --ref main -f version=1.2.3` — after bumping `src/Directory.Build.props` and adding a `## [1.2.3]` section to `CHANGELOG.md` (it becomes the release notes). The workflow tags the commit itself once every build has passed. Self-contained Windows x64 (ZIP) and Linux x64 (tar.gz) builds of the GUI and the CLI, a `SHA256SUMS.txt`, and — once the project is enrolled with SignPath Foundation — Authenticode-signed Windows binaries.
