# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**moist** is a cross-platform video encoder with an Avalonia GUI and a CLI (C# / .NET 8) — a frontend for video encoding, audio encoding, filtering, and muxing. It shells out to FFmpeg/FFprobe/x264/x265/vspipe rather than linking codec libraries. Inspired by MeGUI, modernized for cross-platform use.

## Build, Run & Test

All commands run from `src/` (the solution lives there, not at the repo root):

```bash
cd src
dotnet restore
dotnet build
dotnet run --project Moist.App                               # GUI
dotnet run --project Moist.Cli -- encode in.mkv -o out.mp4   # CLI ('--' separates dotnet's args from ours)
dotnet test                                                  # unit + integration

# One suite / one class / one test
dotnet test Moist.Core.Tests                                 # fast, no external processes
dotnet test Moist.IntegrationTests                           # drives real FFmpeg encodes
dotnet test --filter "FullyQualifiedName~AudioProcessorTests"
dotnet test --filter "FullyQualifiedName=Moist.Core.Tests.AudioProcessorTests.Opus_CustomComplexity_IsIncluded"

# Release / self-contained (what CI publishes)
dotnet publish Moist.App -c Release -r win-x64 --self-contained true -o ../dist
dotnet publish Moist.Cli -c Release -r win-x64 --self-contained true -o ../dist-cli
```

**Prerequisites**: .NET 8 SDK. FFmpeg/FFprobe on PATH for actual encoding — the app builds and runs without them, and Settings can auto-download them.

`Moist.IntegrationTests` **skips itself** when FFmpeg/FFprobe aren't on PATH (see `FfmpegFactAttribute`), so CI stays green on runners without them — but it also means a green local run proves nothing if the tools are missing. Check the skip count.

There is no linter or formatter configured; `dotnet build` warnings are the only static gate.

## Projects

| Project | Role |
|---------|------|
| `Moist.Core` | Jobs, encoders, muxers, media analysis, profiles, settings, tool discovery, plugin loading. **No UI dependency**, and does not reference `Moist.Filters`. |
| `Moist.Filters` | Engine-agnostic filter chain model + FFmpeg / VapourSynth / AviSynth+ script generators. Depends only on `Moist.Core`. |
| `Moist.Services` | `AppServices` (the composition root) and `EncodeJobBuilder`. UI-free, references Core + Filters. **This is the seam that lets the GUI and CLI share one pipeline** — it exists because `Moist.Core` can't reference `Moist.Filters`. |
| `Moist.App` | Avalonia GUI. MVVM via CommunityToolkit.Mvvm. |
| `Moist.Cli` | `moist` command-line binary. No Avalonia dependency — that's the whole point of `Moist.Services`. |
| `Moist.Core.Tests` | Fast unit tests. Never launches an external process. |
| `Moist.IntegrationTests` | Real FFmpeg encodes, verified with ffprobe. Skips without FFmpeg. |

## Architecture

### One pipeline, two front-ends — put shared logic in `Moist.Services`

`EncodeJobBuilder.Build(EncodeRequest, MediaFileInfo?)` is the **only** place that turns settings into a job graph. The GUI's `EncodeTabViewModel.BuildRequest()` and the CLI's `EncodeOptions.Build()` both do nothing but populate an `EncodeRequest` and hand it over.

Keep it that way. Encoding logic added to a ViewModel silently applies to the GUI only, and the CLI drifts. If you're about to write job-construction code in `Moist.App` or `Moist.Cli`, it belongs in `EncodeJobBuilder`.

`EncodeJobBuilder.Validate()` is likewise the single validation entry point, so both front-ends reject the same things with the same message.

### Job pipeline

`EncodeJobBuilder.Build()` is the single place where a user's settings become jobs. It creates:

```
VideoJob  → temp .mkv in Settings.TempDirectory   (skipped in audio-only mode)
AudioJob  → temp .mka
MuxJob    → final output, DependsOn = [videoJob.Id, audioJob.Id]
```

`MuxJob.FilesToCleanup` holds the temp paths. `JobQueue` deletes them **only when the job reaches `Completed`** — failures preserve temps for debugging and retry.

Audio-only mode (`EncodeRequest.AudioOnly`) short-circuits this: no VideoJob, no MuxJob, and the AudioJob writes straight to the final path. The output extension comes from `EncodeJobBuilder.AudioFileExtension()`; the GUI re-applies it whenever `Container`, `AudioOnly`, or `SelectedAudioEncoder` changes.

### Codec ↔ container compatibility

`ContainerCompatibility` (in `Moist.Core/Muxers`) holds the allow-lists for what each container can carry under `-c copy`, plus the bitstream filter some containers need. Both front-ends call `EncodeJobBuilder.Validate()` **before** enqueuing, because otherwise the video and audio encodes run to completion and only the final mux fails.

Two things worth knowing before editing those tables:

1. **They were derived empirically, not from spec.** Each combination was muxed and re-probed to confirm the streams survived. Don't "correct" an entry from memory — re-run the experiment.
2. **A zero exit code from FFmpeg does not mean success here.** Muxing HEVC into AVI exits 0 and produces an unplayable `rawvideo` stream. That's why HEVC is absent from AVI's list and why verification must check the probed codec, not the exit code.

`ContainerCompatibility.VideoBitstreamFilter()` handles the related trap: our intermediate file is Matroska, which stores H.264/H.265 length-prefixed, while AVI and MPEG-TS need Annex-B. Without `h264_mp4toannexb` the mux dies with "bitstream malformed, no startcode found". `MuxJob.VideoCodec` exists solely to let the muxer make this decision.

A null codec means "unknown" (audio passthrough copies whatever the source had) and is always permitted — validation must not block what it cannot predict.

### Processor resolution — order-dependent, read this before adding an encoder

`JobQueue` does not map job → processor by key. For each job it walks `_processorFactories` **in registration order**, instantiates each candidate, and takes the first whose `CanProcess(job)` returns true (falling back to singleton `_processors` for third-party/plugin processors). Two consequences:

1. **Encoder IDs are structurally coupled to processor IDs.** `FFmpegVideoProcessor.Id` is `$"ffmpeg-{_codec}"` and `CanProcess` tests `vj.EncoderId == Id`. So `RegisterVideoEncoder("ffmpeg-x264", …, codec: "x264", …)` in `AppServices` only works because the registered ID is exactly `ffmpeg-` + the codec string. Same for audio (`ffmpeg-audio-{codec}`). Break that and the job fails at runtime with "No processor found", not at compile time.
2. **`TwoPassProcessor` is registered before the plain processor** for every video encoder (`AppServices.RegisterDefaultProcessors()`). It wins only because its `CanProcess` additionally requires `Mode == TwoPassBitrate`. Preserve that ordering when touching registration.

Factories, not singletons: processors hold mutable state (`Stopwatch`, frame counters), so a shared instance would corrupt progress across concurrent jobs.

### Tool paths are late-bound through closures

Encoder factories capture `Settings` (the object), not the path string — e.g. `() => new FFmpegVideoProcessor(Settings.FFmpegPath, codec)`. The path is read when the factory runs, i.e. per job, so changing a tool path in Settings takes effect on the next job without restart. `AppServices.MediaAnalyzer` and `PreviewService` achieve the same via lazy properties that rebuild when the path changes. Don't hoist these into constructor-time fields.

### Two-pass encoding mutates the job

`TwoPassProcessor` runs the inner processor twice against the *same* `VideoJob`, temporarily overwriting `OutputFile` (to `NUL`/`/dev/null` for pass 1) and `Settings.CustomArgs` (appending `-pass N -passlogfile …`, or `--pass N --stats …` for `cli-` encoders). A `finally` block restores `OutputFile`, `Name`, `CustomArgs`, and `PixelFormat`. Any new mutable field touched during a pass must be added to both the snapshot and the restore.

Pass 1 additionally passes **`-f null`** for FFmpeg-based encoders. FFmpeg cannot infer a muxer from `NUL` or `/dev/null` (no extension) and fails with "Unable to choose an output format" without it. The standalone x264/x265 CLIs accept those paths natively and must *not* get the flag.

### Filter engines and the FFmpeg-only constraint

`IFilterEngine` implementations turn an engine-agnostic `FilterChain` into a script: FFmpeg → a `-vf` chain string, VapourSynth → `.vpy`, AviSynth+ → `.avs`. Only the FFmpeg engine's output is consumable by `FFmpegVideoProcessor` (it passes the string to `-vf`). VapourSynth/AviSynth scripts need vspipe/avs2pipe piping into a standalone CLI encoder, which is not implemented — so `EncodeTabViewModel.AddToQueue()` **rejects the enqueue** when a non-FFmpeg engine has a non-empty chain. Also note `FFmpegFilterEngine` silently drops filters it doesn't recognize (it records them in `SkippedFilterIds`, which nothing currently reads).

### Profiles: built-ins are materialized to disk

`ProfileManager` writes each of the 7 `GetBuiltInProfiles()` entries to `profiles/*.json` on first run if the file is absent. Editing `GetBuiltInProfiles()` therefore has **no effect on an existing install** — the on-disk copy wins. Delete the JSON to pick up changes.

### Settings must never fail loudly

`MoistSettings.Save()` is called from the window-closing path, so it is written to **never throw** — it sanitizes non-finite doubles, writes via a temp file + move, and reports failure through `Save(out string? error)` instead. Callers that can show a message (the Settings tab) use the `out` overload; shutdown paths use the fire-and-forget one.

This isn't hypothetical defensiveness: `WindowX`/`WindowY` used to default to `double.NaN`, which `System.Text.Json` refuses to serialize. Every save threw, and the unguarded call in `MainWindow.OnClosing` left the process hung on exit with the window un-closable. Nullable doubles now mean "not set". Keep new settings fields JSON-representable, and keep the closing path exception-free.

### Known gap: queue persistence is half-wired

`JobPersistence.Save()` runs on window close (`MainWindow.axaml.cs`) and `Load()` runs at startup, but `MainWindowViewModel.RestorePersistedJobs()` only reports the restored count in the status bar — it does not re-enqueue anything, and `Load()` deletes the file. Treat crash recovery as unimplemented, not broken-and-in-need-of-a-fix, unless asked to finish it.

### Theming is additive

`MoistDark.axaml` is an overlay layered on top of `FluentTheme`, not a replacement. `App.ApplyTheme()` toggles by adding/removing that `Styles` object from `App.Styles`. A new theme means: `.axaml` in `Themes/`, `StyleInclude` in `App.axaml`, entry in `SettingsTabViewModel.ThemeOptions`, branch in `App.axaml.cs`.

## Code Conventions

- C# 12 / .NET 8, nullable enabled, implicit usings, file-scoped namespaces
- Namespaces mirror folders: `Moist.Core.Jobs`, `Moist.App.ViewModels`, `Moist.Filters.Engines`
- PascalCase public, `_camelCase` private; `[ObservableProperty]` on `_camelCase` fields generates the PascalCase property
- MVVM via `[ObservableProperty]` / `[RelayCommand]`; views are pure AXAML with compiled bindings (`x:DataType`)
- Async-first: `Task` + `CancellationToken` throughout job processing
- JSON via `System.Text.Json`, `WriteIndented = true` + `JsonStringEnumConverter`
- Processors are `sealed` by default. `FFmpegAudioProcessor` is deliberately **not** sealed so `AudioProcessorTests.TestableAudioProcessor` can subclass it and expose `BuildArguments()`. Unseal a processor only when adding that kind of test.
- Best-effort cleanup paths (temp files, log rotation, settings save) swallow exceptions by design — match that rather than propagating.

## Common Tasks

| Task | Touch these |
|------|-------------|
| Add a video encoder | Settings class in `Core/Encoders/Video/` → `AppServices.RegisterVideoEncoder()` (ID must be `ffmpeg-{codec}`) → switch in `EncodeJobBuilder.Build()` → codec name in `FFmpegVideoProcessor.GetFfmpegCodecName()` → `ContainerCompatibility.StreamCodecForEncoder()` and the container tables |
| Add an audio encoder | Settings class in `Core/Encoders/Audio/` → `AppServices.RegisterAudioEncoder()` (ID must be `ffmpeg-audio-{codec}`) → `FFmpegAudioProcessor.GetCodecSpecificArgs()` + `GetFfmpegCodecName()` → `EncodeJobBuilder.CreateAudioSettings()` + `AudioFileExtension()` → `ContainerCompatibility` |
| Add an encode option | `EncodeRequest` first, then `EncodeJobBuilder`, then surface it in *both* `EncodeTabViewModel.BuildRequest()` and `EncodeOptions.Build()` + `Help.cs` |
| Add a CLI command | `Commands.cs` + dispatch in `Program.Main` + a section in `Help.cs` |
| Test encoder arguments | `Core.Tests/AudioProcessorTests.cs` — subclass the processor, call `BuildArguments()`, assert on the string. No process launch. |
| Test real encoding | `Moist.IntegrationTests` — use `[FfmpegFact]`/`[FfmpegTheory]` so it skips without FFmpeg, and assert on the **probed** output, not the exit code |
| Add a filter | `FilterDefinition` in the engine's `GetAvailableFilters()` + the emit branch in its `GenerateScript()` |
| Add a filter engine | Implement `IFilterEngine`, register in `AppServices.RegisterDefaultFilters()` — but see the FFmpeg-only constraint above |
| Add a built-in profile | `EncodingProfile.GetBuiltInProfiles()` (won't apply to existing installs) |
| Add a setting | `Core/Settings/MoistSettings.cs` + `SettingsTabViewModel` (load in ctor, write back in the save path). Must be JSON-representable. |
| Add a tool to auto-discovery | `ToolDiscovery.KnownTools` + `AppServices.GetConfiguredToolPath`/`SetConfiguredToolPath` |

## Data & Runtime Paths

All under `%AppData%/moist` (Windows) / `~/.config/moist` (Linux/macOS) — the code uses `SpecialFolder.ApplicationData` uniformly:

- `settings.json` — app settings. Corrupt settings are deleted and defaults reloaded on startup failure.
- `profiles/*.json` — one file per profile, built-ins included
- `plugins/*.dll` — scanned recursively at startup; `IPlugin` types with a parameterless ctor are instantiated and given `Register(EncoderRegistry, JobQueue)`. Load failures are logged and skipped.
- `tools/` — auto-downloaded tool binaries
- `logs/moist_<timestamp>.log` — per-session; last 10 kept. Completed/failed job output is appended here via `AppServices.WriteJobLogToFile`.
- `queue.json` — written on close, consumed once on startup (see the persistence gap above)

Temp encodes go to `Settings.TempDirectory`, default `%TEMP%/moist`.

## CI/CD

- `ci.yml` — push/PR to `main`. Builds Debug + Release and runs tests on **self-hosted** runners (group `Default`, labels `Windows`/`Linux`). `DOTNET_INSTALL_DIR` is pinned into the workspace because the runners are self-hosted. A non-blocking step reports whether FFmpeg is present; if it isn't, the integration suite skips and CI still passes — install FFmpeg on the runners to actually exercise it. macOS jobs exist but are commented out.
- `release.yml` — on `v*` tags. Self-contained single-file builds of both the GUI and the CLI for Windows x64 (ZIP, plus a framework-dependent GUI ZIP) and Linux x64 (tar.gz), then a GitHub release. CLI archives are named `moist-cli-*`. Tags containing `-` are marked prerelease.
