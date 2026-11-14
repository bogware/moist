# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**moist** is a cross-platform video encoder with an Avalonia GUI and a CLI (C# / .NET 10) — a frontend for video encoding, audio encoding, filtering, and muxing. It shells out to FFmpeg/FFprobe/x264/x265/vspipe rather than linking codec libraries. Inspired by MeGUI, modernized for cross-platform use.

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

The Docker image is the one thing that builds from the **repo root**, not `src/` (the `Dockerfile` `COPY`s `src/...`), and it packages `Moist.Cli` only — the GUI is never containerized:

```bash
docker build -t moist .                                          # from repo root
docker run -p 7788:7788 -v /media:/media -e MOIST_TOKEN=secret moist   # defaults to `moist serve`
```

**Prerequisites**: .NET 10 SDK. FFmpeg/FFprobe on PATH for actual encoding — the app builds and runs without them, and Settings can auto-download them.

`Moist.IntegrationTests` **skips itself** when FFmpeg/FFprobe aren't on PATH (see `FfmpegFactAttribute`), so CI stays green on runners without them — but it also means a green local run proves nothing if the tools are missing. Check the skip count.

There is no linter or formatter configured; `dotnet build` warnings are the only static gate.

## Projects

| Project | Role |
|---------|------|
| `Moist.Core` | Jobs, encoders, muxers, media analysis, profiles, settings, tool discovery, plugin loading. **No UI dependency**, and does not reference `Moist.Filters`. |
| `Moist.Filters` | Engine-agnostic filter chain model + FFmpeg / VapourSynth / AviSynth+ script generators. Depends only on `Moist.Core`. |
| `Moist.Services` | `AppServices` (the composition root) and `EncodeJobBuilder`, plus the engine-agnostic `WatchFolderService` and `LibraryHealthChecker`. UI-free, references Core + Filters. **This is the seam that lets the GUI and CLI share one pipeline** — it exists because `Moist.Core` can't reference `Moist.Filters`. |
| `Moist.App` | Avalonia GUI. MVVM via CommunityToolkit.Mvvm. |
| `Moist.Cli` | `moist` command-line binary (`encode`, `batch`, `probe`, `compare`, `crf-search`, `watch`, `health`, `serve`, `list`, `tools`), including the `serve` HTTP server (`MoistHttpApi`). No Avalonia dependency — that's the whole point of `Moist.Services`. |
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

### Bit depth: never hardcode a profile

`-profile:v` is sent **only when the user explicitly sets one**. `X264Settings.Profile`
and `X265Settings.Profile` default to null for a reason: they used to default to
`"high"`/`"main"`, which are 8-bit-only, so every 10-bit source — HDR, anime, Blu-ray
remuxes, ProRes masters — died with *"profile doesn't support a bit depth of 10"*.
Left unset, the encoder picks a profile matching the actual bit depth.

`VideoBitDepth.Auto` (the default) likewise emits no `-pix_fmt`, so the source's depth
carries through. Only `Eight`/`Ten`/`Twelve` force one. If you add a codec-specific
setting that touches profile or pixel format, keep both opt-in.

Source bit depth comes from `FFprobeMediaAnalyzer.BitDepthFromPixelFormat`, which reads
the plane marker rather than trailing digits — `nv12` and `yuyv422` end in digits that
describe chroma layout, not depth.

### Streams that only exist in the container

The mux reads from intermediate files, so anything that lives only in the *source*
container is lost unless deliberately carried over. `MuxJob.SourceFile` exists purely
so the muxer can add the original as an extra input and copy:

- **chapters** (`-map_chapters`) — only for MKV/MP4/MOV; other containers can't hold them.
  When not copying, moist passes `-map_chapters -1`, because FFmpeg's default is to take
  them from whichever input happens to have some.
- **attachments** (`-map <i>:t?`) — subtitle fonts. **Matroska only**; mapping them into
  anything else fails the mux outright.
- **global tags** (`-map_metadata`).

### Silent sources

A video-only file gets **no audio jobs at all**. Building one anyway produced
`-map 0:a:0` against a file with no audio streams, which failed the whole encode —
screen recordings, animation renders and action-cam clips all hit this.

`EncodePlan.AudioJobs` may therefore be empty and `EncodePlan.Audio` may be null.
`EncodeJobBuilder.Validate` takes an optional `MediaFileInfo` so it can reject
audio-only on a silent source with a real explanation rather than failing later.

### Multiple audio tracks

`EncodeRequest.AudioTracks` is a list. Each entry becomes its own `AudioJob` writing to
its own temp file, and its own `AudioTrackInput` in the mux carrying the source's
language and title. Per-track metadata is indexed `-metadata:s:a:N` (Nth *audio* stream),
not a global stream number, which would shift depending on whether video is present.

Audio-only output uses the first selected track — a single file can't hold several
encoded tracks without a mux step.

### Streams that don't start together: anchor to the first video frame

A broadcast capture or a stream-copied cut starts its video and audio at *different*
timestamps — the recording begins mid-GOP, so the first decodable video frame is typically
~1 s after the first audio packet (MeTV `.ts` captures measured 0.6–1.2 s). Because video and
audio are encoded in separate jobs, each temp file starts at its own first packet, and a naive
mux plays the picture ahead of the sound by the whole stagger. `FFprobeMediaAnalyzer` reads
every stream's `start_time`, and `EncodeJobBuilder` anchors each audio track to the first
video frame: a lead is trimmed at encode time (`AudioJob.TrimStart` → input-side `-ss`), a lag
is delayed in the mux (`AudioTrackInput.DelayMs` → `-itsoffset`). The synthesized fixtures
start all streams together, which is why this went unnoticed; `AvSyncTests` now uses a
staggered fixture. Known gap: subtitles muxed straight from the source file stay on the
source's timeline (a negative `-itsoffset` on one mux input risks `avoid_negative_ts` shifting
the whole output), so on a staggered source they can run early by the stagger.

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

### Chunked encoding: frames, not seconds

Chunks are addressed in **frame numbers**, and each is encoded with `-frames:v <count>`
(`VideoJob.TrimFrames`). This is not fussiness: at 23.976 fps a duration in seconds
cannot address a frame boundary, and the error accumulates across chunks until the
joined file has gained or lost frames. A frame count cannot drift. `TrimDuration` still
exists for target-quality sampling, where exactness doesn't matter, but chunking must
use `TrimFrames`.

`ChunkPlan.Validate()` enforces the one property that matters — the chunks tile the
source exactly, no gaps and no overlaps — and `ChunkPlanner.Plan` refuses to return a
plan that fails it. A gap loses footage, an overlap repeats it, and both are silent.

**Verification is not optional.** FFmpeg exits zero having written a file that is a few
frames short, so `FFmpegConcatProcessor` counts the frames in the joined result and
fails the job on a mismatch. It counts by decoding (`-count_frames`) rather than reading
container metadata, which lies often enough after a concat to defeat the point. Resume
applies the same test: a leftover chunk is reused only if it decodes to exactly the
expected frames, so a half-written file from a kill is discarded rather than spliced in.

The chunk directory name is a hash of the source identity *and* every setting that
shapes a chunk's contents, including the boundary positions — otherwise a resumed run
could splice together chunks encoded with different settings.

Don't expect large speedups. Chunking only recovers CPU the encoder wasn't already
using; measured here, x265 `slow` leaves ~23% headroom on 16 cores and SVT-AV1 leaves
none (chunked is *slower*). It stays valuable for poorly-threaded encoders, very high
core counts, and resumability. It is off by default.

### Per-scene optimisation: compressibility is not perceptual tolerance

`PerSceneOptimizer` gives every chunk its own CRF so quality is even across the file,
rather than bitrate being even. It measures each scene's complexity cheaply (bytes per
frame at a fixed CRF), searches a few scenes spanning that range properly, fits CRF
against **log** complexity — complexity spans orders of magnitude while the useful CRF
range spans a few points — and predicts the rest.

**The prediction is then checked.** This is the part not to remove. Complexity is
measured as compressibility, and the two come apart badly: a SMPTE bars pattern
compresses to almost nothing, so the model handed it CRF 42, and it scored **80**
against a target of 93 because sharp edges are exactly what a perceptual metric
punishes when quantised hard. Every modelled scene is now measured at its assigned CRF
and re-searched if it misses — one cheap check when the model is right, a real search
only where it was wrong.

Calibration scenes are chosen by spread in *complexity*, not position in the timeline;
ten similar scenes teach the model nothing that one would not.

When judging whether this helps, compare at equal **worst-case** quality, not equal
mean. A fixed CRF's average flatters it by hiding the one scene that looks bad — which
is the scene a viewer actually notices.

### Quality metrics: framesync will lie to you

Every metric filter (`libvmaf`, `xpsnr`, `ssim`, `psnr`) is built on FFmpeg's
**framesync**, which by default keeps running after the shorter input ends and holds
its last frame. Comparing a 5-second sample against a 40-second source therefore
scores a frozen frame against moving video for the remaining 35 seconds — one
near-lossless clip measured **31** instead of 98.

`QualityMeasurer.FrameSync` (`shortest=1:repeatlast=0`) is appended to every metric
filter and must stay there. There's a test asserting it for all four metrics.

Two more things `QualityMeasurer` handles that are easy to get wrong:
- **`settb=AVTB` on both inputs.** Without it FFmpeg warns about mismatched timebases
  whenever the two files came from different muxers, and the results are suspect.
- **libvmaf's `log_path` runs through filter-argument escaping**, which is miserable
  for Windows paths (`C:\…` needs both the backslashes and the colon escaped). The
  measurer sidesteps it by running FFmpeg with its working directory set to a scratch
  folder and using a bare filename.

**Never use `testsrc2` to test quality behaviour.** It's synthetic high-frequency
noise; VMAF scores it in a narrow band (~71-75) whatever the CRF, so quality looks
unresponsive to settings. `MediaFixture.NaturalVideo` (mandelbrot) behaves like real
footage — CRF 14/24/32/40 gives VMAF 97.5/88.8/69.6/41.6.

### Target-quality search

`TargetQualitySearch` encodes a few short spans rather than the whole file, then
bisects over CRF — interpolating between the two bracketing probes instead of blindly
halving, since quality against CRF is close to linear over a short span.

Two decisions to preserve:

1. **Search uses the encoder and preset the real encode will use.** Probing with a
   fast proxy (NVENC standing in for x265) was considered and rejected: the CRF that
   hits VMAF 95 on one encoder doesn't hit it on another, so it would need a
   content-dependent calibration offset. Sampling already provides the speed.
2. **Samples skip the outer 5%** of the source. Titles, logos and black frames score
   nothing like the actual content and would drag the estimate around.

Sampling accuracy is verified end-to-end: an integration test searches, encodes the
full file at the chosen CRF, and asserts the measured score is near the prediction.

### Hardware encoders are a separate world

`FFmpegHardwareVideoProcessor` is deliberately *not* a branch inside
`FFmpegVideoProcessor`, because almost nothing transfers: GPU encoders reject `-crf`,
use their own preset scales, and each vendor spells rate control differently
(`-rc vbr -cq`, `-global_quality`, `-rc cqp -qp_i`, an inverted `-q:v`, `-rc_mode CQP`).

Three things to know before touching this:

1. **A listed encoder is not a working one.** `ffmpeg -encoders` reports what the binary
   was compiled with — a stock Windows build advertises NVENC, QSV, AMF and VA-API on a
   machine with none of them. `HardwareCapabilities.DetectAsync` encodes a few frames and
   keeps only what succeeds. Never register or offer a hardware encoder on the strength
   of the listing alone.
2. **`TwoPassProcessor` must keep refusing hardware jobs.** They have no `-pass`; NVENC
   does its analysis in one run via `-multipass`. The guard is a type check on
   `HardwareVideoSettings` — keep it if you refactor the settings hierarchy.
3. **Encoder ids encode their codec**: `hw-<vendor>-<codec>`.
   `ContainerCompatibility.StreamCodecForEncoder` parses that, so a new hardware encoder
   participates in container validation automatically — but the id format must hold.

VA-API additionally needs `-vaapi_device` before the input and `format=nv12,hwupload`
appended to the filter chain, since it encodes from GPU memory.

Only NVENC is verified on real hardware here (RTX 4080). QSV, AMF, VideoToolbox and
VA-API are implemented from their documented interfaces and correctly *detected* as
absent, but their encode paths are unproven.

### Hung tools are killed, not waited on

Every external tool runs under `ProcessWatchdog` (`Moist.Core/Jobs/`): if neither pipe
produces a line for `StallTimeout` (15 min by default) the process tree is killed and the job
fails with a `TimeoutException` that says so. `CommandLineProcessor` applies it to every
encoder, muxer and probe; `RealEsrganVideoProcessor` and `RifeVideoProcessor` call it directly
for each of their steps because they don't inherit from `CommandLineProcessor`. The budget is
deliberately generous — a false positive kills real work — and a processor can override
`StallTimeout` if its tool is legitimately quiet for long stretches.

The watchdog only works because **every tool moist runs emits a heartbeat**: ffmpeg via
`-progress pipe:1` (or its stats line), x264/x265 via their status line, Real-ESRGAN and RIFE
via per-frame percentages. If you add a tool invocation, make sure it reports continuously —
or that you pass `-progress`/`-stats` — otherwise the watchdog will kill it mid-work. The
Real-ESRGAN frame *extraction* is the cautionary example: it ran ffmpeg with no progress
output for minutes at a time, which looked identical to a hang.

### Two-pass encoding mutates the job

`TwoPassProcessor` runs the inner processor twice against the *same* `VideoJob`, temporarily overwriting `OutputFile` (to `NUL`/`/dev/null` for pass 1) and `Settings.CustomArgs` (appending `-pass N -passlogfile …`, or `--pass N --stats …` for `cli-` encoders). A `finally` block restores `OutputFile`, `Name`, `CustomArgs`, and `PixelFormat`. Any new mutable field touched during a pass must be added to both the snapshot and the restore.

Pass 1 additionally passes **`-f null`** for FFmpeg-based encoders. FFmpeg cannot infer a muxer from `NUL` or `/dev/null` (no extension) and fails with "Unable to choose an output format" without it. The standalone x264/x265 CLIs accept those paths natively and must *not* get the flag.

### The standalone encoders run a shell pipeline

`cli-x264` and `cli-x265` read raw frames on stdin, so they run as `ffmpeg … -f yuv4mpegpipe - | x264 …` through a shell. All of that lives in `ShellPipeline` (`Moist.Core/Jobs/`) — put shell handling there, not in a processor, because it was duplicated in both and both were wrong.

Things that path gets wrong if you touch it carelessly:

- **`cmd.exe` strips the first and last quote after `/c`.** A pipeline begins with a quoted tool path, so it needs an *extra* enclosing pair — `/s /c "…"` — or the command arrives mangled and fails with no usable error. Use `ShellPipeline.Apply`, which also uses `ArgumentList` on Unix so the command is never re-parsed.
- **Trims are not optional.** Chunked encoding, target-quality search and per-scene optimisation encode *pieces* of the source via `TrimStart`/`TrimFrames`/`TrimDuration`. An encoder ignoring them encodes the whole file for every chunk and every probe, silently.
- **Both encoders default to 8-bit output** regardless of what the pipe carries, so `--output-depth` has to be sent explicitly for 10/12-bit. It's only sent when it differs from 8, since single-depth builds reject the option outright.
- **FFmpeg before 7 adds one trailing frame** when converting to the constant rate a y4m pipe
  requires (Debian 12's 5.1, Ubuntu 24.04's 6.1). Don't "fix" it with `-fps_mode passthrough` — CFR conversion
  is what keeps a VFR source in sync once the untimed x265 stream is wrapped at the nominal rate. Chunks and
  trims are capped by `-frames:v`, so only a whole-file encode on an old FFmpeg sees the extra 42 ms.
- **B-frames vs the wrap.** FFmpeg can only number a raw HEVC stream's packets in *decode*
  order; with B-frames that gives presentation timestamps 0, .042, .167, .125… (right frames, wrong
  times — invisible to frame-count and duration checks, caught with a flat-luma-per-frame source).
  `X265StreamWrap`: `NoBFrames` (default, `--bframes 0`, FFmpeg wrap), `Mkvmerge` (keeps B-frames,
  mkvmerge reads picture order), `Auto`. The decision is cached per job so both two-pass passes agree.
  The FFmpeg wrap is **verified** (duration = packets ÷ rate) and retried with a millisecond `setts`
  expression, because FFmpeg < 7 hands Matroska the filter's output in ms regardless of its time base.
- **x265 has no muxer** — it writes a raw HEVC elementary stream with no timing at all, so `OnAfterSuccessAsync` wraps it while the source frame rate is still known. On FFmpeg 8 the raw demuxer emits packets with no timestamps and Matroska refuses them, so they're synthesised with `-bsf:v setts=ts=N*den/num/TB`. Don't "simplify" this to an MP4 remux: that exits 0 while dropping frames.
- **`BuildArguments` is what actually runs**, composed into the pipeline by `OnBeforeStart`. Don't build a second copy of the arguments there — that's how the x264-specific settings got silently dropped for a year.

`CommandLineProcessor` logs the command *after* `OnBeforeStart`, so what's in the log is what ran.

### Filter engines and the FFmpeg-only constraint

`IFilterEngine` implementations turn an engine-agnostic `FilterChain` into a script: FFmpeg → a `-vf` chain string, VapourSynth → `.vpy`, AviSynth+ → `.avs`. The FFmpeg engine's output goes straight to `FFmpegVideoProcessor` (`-vf`). **VapourSynth is now wired too**: a job whose `FilterEngineId == "vapoursynth"` is caught by `VapourSynthPipeProcessor`, which writes the `.vpy` and runs `vspipe --y4m script.vpy - | ffmpeg …` via `ShellPipeline` — the same producer|consumer machinery behind the standalone x264/x265 encoders. That's the seam that makes vs-mlrt ML super-resolution (`vs_superres`, emitting `core.ort/trt/ov.Model`) reachable. The CLI selects the engine with `--filter-engine ffmpeg|vapoursynth`; the GUI now rejects only engines that are neither (i.e. **AviSynth**, whose avs2pipe piping still isn't built). Note the generated `.vpy` calls `lsmas.LWLibavSource` and any vs-mlrt node, all third-party plugins that ship with neither VapourSynth nor moist — so the VS path is correct-by-construction here but only runs where the user has that stack installed. Also note `FFmpegFilterEngine` silently drops filters it doesn't recognize (it records them in `SkippedFilterIds`, which nothing currently reads).

### GPU super-resolution reaches across the filter/processor seam

The `superres` filter is the one filter whose `-vf` string isn't self-contained. It runs a
neural GLSL shader (FSRCNNX for live action, Anime4K for anime) through FFmpeg's `libplacebo`,
which needs three things the rest of the chain doesn't:

- **A Vulkan device before `-i`.** `FFmpegVideoProcessor` — and the hardware one, so NVENC+superres
  works — add `-init_hw_device vulkan=vk:0 -filter_hw_device vk` whenever the generated filter
  script contains `libplacebo` (`GpuShaders.UsedInFilterScript`). VA-API is excluded: its own
  render device already owns the filter chain.
- **`hwupload`/`hwdownload` around the filter.** `BuildSuperResFilter` emits the whole
  `format=yuv420p,hwupload,libplacebo=…,hwdownload,format=yuv420p` sub-chain itself, so it still
  composes into the linear `-vf` like any other filter.
- **The shader found by a bare filename.** The shaders live in `Moist.Core/Shaders` (copied to
  output — `GpuShaders.Directory`), and the processor sets FFmpeg's working directory there so
  `custom_shader_path=FSRCNNX_x2_8-0-4-1.glsl` resolves without the drive-colon filter escaping
  that makes libvmaf's `log_path` miserable — the same trick `QualityMeasurer` uses.

**It degrades instead of failing.** `SuperResDetector` runs a tiny libplacebo encode once and
caches it; `AppServices` wires it into `FFmpegFilterEngine.IsSuperResAvailable`. With no usable
Vulkan GPU, `superres` emits a plain `scale=-2:H:flags=spline` instead — so the built-in
"AI Upscale SD to HD (GPU)" profile still produces HD on a GPU-less machine, just without the
neural upscale. A listed libplacebo is not a working one, same lesson as the hardware encoders.

Order still matters: clean before you enlarge. The AI profile runs `deblock → hqdn3d → superres
→ cas → deband`, because an AI upscaler sharpens whatever you feed it, block noise included.

### Real-ESRGAN is a processor, not a filter

The deep-model upscaler can't be a filter — Real-ESRGAN only processes still images — so
`RealEsrganVideoProcessor` owns the whole VideoJob when `VideoJob.AiUpscaleModel` is set:
extract cleaned frames → run the model → encode. It's registered **before** the normal video
processors in `AppServices.RegisterDefaultProcessors`, because an AI-upscale job still names a
real encoder (x265, …) that `FFmpegVideoProcessor.CanProcess` would otherwise claim; it wins by
additionally requiring `AiUpscaleModel`.

Things that path gets right on purpose:

- **The filter chain is split.** The profile carries only the pre-clean (`deblock`, `hqdn3d`) in
  its `Filters` — that becomes the job's `FilterScript`, applied during frame *extraction*. The
  scale-to-height, `cas` and `deband` finish are added by the processor during the *encode*,
  because cleaning has to happen on the native frames before the model enlarges them.
- **Frames are verified, not trusted.** It counts the extracted frames and asserts the encoded
  output matches — a re-encode that silently drops frames desyncs the audio. JPEG intermediates
  (a 4× frame is multi-MB as PNG) keep the scratch bounded.
- **Long sources stream in windows.** Above `WindowFrames`, it copies a slice of frames, upscales
  and encodes that window to a segment, drops the scratch, repeats, then concats — so the huge
  upscaled frames never all land at once and it handles full films, not just clips. The work dir is
  keyed on source+settings (`WorkKey`) and only cleaned on success, so an interrupted run **resumes**
  by reusing finished segments.
- **The encode rate is measured, not assumed.** The pre-clean runs *before* the frames hit
  disk, and a cadence filter in it (`ivtc`, `fps`, `decimate`) changes the rate those frames must
  be encoded at. `MeasureOutputRateAsync` runs a dozen frames through the same chain with
  `settb=AVTB,showinfo` appended and snaps the median frame period to an exact rational
  (`ResolveOutputFrameRate`), so an ivtc'd 59.94 capture encodes at `24000/1001` rather than the
  source's `60000/1001` — which played the picture at 2.5× against the audio. Don't replace this
  with ffmpeg's `-progress` counters: `frame=` and `out_time=` lag each other under threaded PNG
  encoding and the slope between them was off by 9%. **FFmpeg before 7 retimes differently:** its `decimate` leaves the
  survivors on the 30i grid (33/33/33/67 ms), so `MeasureFromShowinfo` flags uneven spacing and the
  resolver snaps the span average to a simple multiple of the source rate instead of the median. Trims (`TrimStart`/`TrimFrames`/
  `TrimDuration`) are applied to extraction and are part of `WorkKey`, so a snippet preview
  neither upscales the whole film nor leaves a scratch the full encode would "resume" from.
- **The finish is the profile's.** `EncodingProfile.AiFinishFilters` (rendered by `ProfileFinish`,
  shared by GUI and CLI, carried as `VideoJob.AiFinishFilterScript`) runs on the enlarged frames after
  the scale; empty keeps the default CAS 0.4 + deband. Film grain *must* live here, never in the
  pre-clean — the model scrubs whatever grain it is fed. The finish and the tune are in `WorkKey`, and
  `BuildEncodeArgs` passes `-tune` for libx264/libx265 (it used to drop it, so `tune=grain` profiles
  lost their grain on exactly this path).
- **It's a tool, so it's optional.** `realesrgan-ncnn-vulkan` is in `ToolDiscovery.KnownTools`
  (auto-downloadable), and the processor refuses with a pointer to Settings when it's absent.

`RifeVideoProcessor` is the same shape for **frame-rate** work: RIFE synthesizes in-between frames,
so it too is a frame-based processor (handles a job whose `RifeTargetFps > 0`, reusing Real-ESRGAN's
extract/codec builders). It's the `--fps-mode rife` path; `dup`/`motion` framerate changes stay
filters (`fps` / `minterpolate`) added to the `-vf` in `EncodeOptions.BuildFilters`.

### Profiles: built-ins are materialized to disk

`ProfileManager` writes each of the 18 `GetBuiltInProfiles()` entries to `profiles/*.json` on first run if the file is absent. Editing `GetBuiltInProfiles()` therefore has **no effect on an existing install** — the on-disk copy wins. Delete the JSON to pick up changes.

### Tool downloads are pinned and hash-verified

Every `ToolDiscovery.KnownTools` entry with `DownloadUrls` also has `ExpectedSha256`; a mismatch
aborts the install and deletes the file. Pin **immutable** URLs only. BtbN deletes its *daily*
FFmpeg autobuilds after a few weeks (the old Linux pin 404'd) but keeps each month's last build, so
pin a month-end tag's release-branch asset. MKVToolNix's Windows portable is LZMA 7z, which Windows'
`tar.exe` can't read; `SevenZipExtractor` (SharpCompress) unpacks it with an explicit
outside-the-folder check on every entry.

### Settings must never fail loudly

`MoistSettings.Save()` is called from the window-closing path, so it is written to **never throw** — it sanitizes non-finite doubles, writes via a temp file + move, and reports failure through `Save(out string? error)` instead. Callers that can show a message (the Settings tab) use the `out` overload; shutdown paths use the fire-and-forget one.

This isn't hypothetical defensiveness: `WindowX`/`WindowY` used to default to `double.NaN`, which `System.Text.Json` refuses to serialize. Every save threw, and the unguarded call in `MainWindow.OnClosing` left the process hung on exit with the window un-closable. Nullable doubles now mean "not set". Keep new settings fields JSON-representable, and keep the closing path exception-free.

### Queue persistence stores the request, not the jobs

`EncodeQueueStore` persists the `EncodeRequest`, because `EncodeJobBuilder.Build` turns
one deterministically back into the whole job graph. The previous implementation stored
a flattened per-job snapshot (id, name, input, output) which carried no encoder settings
and therefore could not be restored at all — if you find yourself adding fields to a
persisted *job*, that's the wrong layer.

Entries clear themselves: `AppServices` watches `JobStatusChanged` and calls
`NotifyJobFinished` when a tracked final job settles, whether it succeeded or not.
Restoring re-records under the *new* final job id, since ids are regenerated on rebuild.

Reading the pending list does not consume it — the old version deleted the file on load,
so a crash mid-restore lost the queue for good.

### Watch folders, health checks and the server reuse the pipeline — they don't reimplement it

Three headless surfaces were added on top of the existing job graph, not beside it. The
rule from "One pipeline, two front-ends" extends to all of them: an `EncodeRequest` still
goes through `EncodeJobBuilder.Validate`/`Build`, so nothing they encode can drift from
what the GUI and CLI produce. The watcher and health checker are UI-free and live in
`Moist.Services` (`WatchFolderService`, `LibraryHealthChecker`); the server lives in
`Moist.Cli` (`ServeCommand`, `MoistHttpApi`). None is a project of its own.

**Watch folders (`moist watch`).** `WatchFolderService` **polls** on an interval rather
than using `FileSystemWatcher` — network shares silently drop change events, and this
feature exists to run against exactly those. A file is handed to the encoder only once its
size *and* last-write time have held still for `StableFor` (default 30s): a file still
being copied has a valid header and would encode to a silently truncated output with no
error. Handled files are recorded in `watch-<hash>.json` (beside `settings.json`) so a
restart doesn't re-encode them, and the source is moved or deleted **only after the encode
succeeds** (`WatchPostAction`). `WatchFolderOptions.Validate()` rejects an output directory
inside the watched folder — otherwise it would encode its own output forever.

**Health checks (`moist health`).** `LibraryHealthChecker` is shallow by default —
ffprobe headers only, flagging unreadable / no-video / zero-duration files. `--deep`
additionally decodes every file (`ffmpeg -xerror -f null -`) and counts frames, because
mid-file corruption has a perfectly good header and a plausible duration and is invisible
to a header read. It exits non-zero when any fault is found, so it can gate a cron job or
CI. `NoAudioStream` is informational, not a fault (`LibraryIssue.IsFault`).

**The server (`moist serve`).** Built on `System.Net.HttpListener`, deliberately not
ASP.NET Core, to keep the self-contained binary a single dependency-free file. It is
authenticated by default (bearer `--token`, token compared in fixed time) and bound to
loopback by default; `--no-auth` is allowed only on loopback, and any non-loopback bind
requires a token, because an encode names its own input and output paths — that is a
filesystem trust boundary. `POST /api/encode` takes a JSON body, converts it **back into
CLI arguments** and runs them through the same `EncodeOptions` parser the CLI uses, so the
option set cannot drift from the command line — but **only allow-listed options are accepted**
(`ServePolicy.AllowedOptions`, in `Moist.Cli/ServePolicy.cs`). Several CLI options are code
execution in a stranger's hands: `--ffmpeg`/`--ffprobe` name the binary to run, `--video-args`/
`--audio-args` reach a `cmd`/`sh` pipeline, `--temp-dir`/`--output-dir`/`--suffix` place files,
`--filter-engine vapoursynth` runs generated Python. A new CLI option is refused over the API until
someone adds it to the list on purpose. Paths (input, output, `--subtitle`, `/api/probe`) go through
`ServePolicy.Resolve`: under a `--root` when roots are set (mandatory for a non-loopback bind) and
never inside `MoistPaths.ConfigRoot`. The token can come from `MOIST_TOKEN` so wrappers never put it
on argv. Progress is mirrored into a dictionary from
`JobQueue.JobProgressUpdated` because a `Job` carries no progress of its own. The Docker
image is purpose-built to run this command (see CI/CD).

### HDR detection can't trust the colour tags

Matroska doesn't necessarily surface `color_transfer` at container level; the HDR10 test
fixture reports `unknown` while the bitstream is plainly PQ. `FFprobeMediaAnalyzer`
therefore also reads frame **side data**, which is where mastering display, content light
level, HDR10+ and Dolby Vision live. That's why the ffprobe call passes `-show_frames
-read_intervals "%+#1"` — reading only `-show_streams` misses every one of them, and
`VideoStreamInfo.IsHdr` ORs the side-data flags in for the same reason.

### Filter arguments need escaping, not quoting

FFmpeg's filter parser treats `:` as an option separator, so a Windows path inside
`subtitles=` breaks the whole graph. `EncodeJobBuilder.EscapeForFilter` flips separators
and escapes the drive colon. Same class of problem as libvmaf's `log_path`, solved
differently there (working directory) because that one is a bare filename.

Burn-in and chunked encoding are **mutually exclusive**, and `Warnings()` says so: each
chunk seeks independently, so subtitle timings would drift after the first chunk. So are
the cadence filters (`ivtc`, `mpdecimate`): they change the frame count, which the chunk
frame-count verification would then reject.

### Telecined film: `ivtc`, not `mpdecimate`

`ivtc` exists because a pixel-difference decimator cannot follow a 3:2 film cadence. On
bob-deinterlaced 60p broadcast captures (MeTV-style 1280×720p59.94 MPEG-2 of 24 fps film),
`mpdecimate` landed anywhere between 26 and 42 fps depending on the episode's grain and
chroma noise, and pushing the thresholds started deleting real frames before it reached the
duplicates. Weaving each pair of 60p frames back into one 30i frame
(`tinterlace=mode=interleave_top`, or `interleave_bottom` for BFF) and running
`fieldmatch,decimate` reunites the real fields — `idet` reads the woven stream as pure
TFF — and lands on 23.98 fps on every episode tried, with nothing interpolated. Prefer
`ivtc` for telecined film; keep `mpdecimate` for genuinely duplicated frames.

**Dropouts collapse under `decimate`.** Over-the-air captures have reception gaps: one
Gunsmoke episode had 16 video pts jumps totalling 2.2 s, with matching audio gaps. `decimate`
rewrites timestamps assuming a constant input rate, so the gaps vanished from the video while
the separately encoded audio kept them — the picture led the sound by 1.8 s at minute 55. The
`ivtc` chain therefore starts with `fps=source_fps`, which duplicates frames across a gap so
the timeline survives: a dropout becomes a short freeze over silent audio, in sync. The 75 s
test clips had no dropouts, which is why only a full-length capture caught it — verify sync by
matching a late-episode output frame back to its source frame (PSNR peak over a ±10 s window),
not by comparing durations: decimate's fixed 4:5 ratio keeps the total length plausible.

**Not `tinterlace=mode=merge`.** It recovers the same cadence, which is how it passed the
first frame-count experiment — but it stacks the pair into a *double-height* frame, so a
later height-based resize (`scale=-2:1080`) silently halves the width. The 828×1080 outputs
it produced were caught by probing the result's dimensions, not by counting its frames:
when a chain changes frame geometry, probe `width`×`height` on the output, not just `nb_frames`.

### HDR10+ / Dolby Vision passthrough is experimental and opt-in

Dynamic HDR metadata is detected and warned about, and — only when
`EncodeRequest.PreserveDynamicHdr` is set (`--preserve-dynamic-hdr`, or the GUI toggle) —
`DynamicHdrPreserver` (`Moist.Core/Media`) tries to carry it across the re-encode: demux
HEVC → `dovi_tool extract-rpu` / `hdr10plus_tool extract` from the source → `inject-rpu` /
`inject` into the encoded stream → remux. The muxer runs it in `OnBeforeStart` (after the
video job finished, before the mux) against the temp video; `AppServices` threads the tool
paths in late-bound like every other tool. Both tools auto-download from Settings.

**The remux needs mkvmerge.** The enriched stream is raw HEVC — untimed, and libx265 encodes
with B-frames — so FFmpeg can't put it back in a container (FFmpeg 8 refuses untimed packets,
exit -22). `TryRemuxWithMkvmergeAsync` reads the encoded file's frame duration (`mkvmerge -J`)
and remuxes with `--default-duration`, carrying every non-video track over with `-D`. Without
MKVToolNix the FFmpeg remux is still attempted and the failure message says to install it.
The scratch area lives beside the encoded file (moist's temp dir), not `Path.GetTempPath()`.

**Verified on synthetic content only.** End-to-end runs built HDR10 PQ clips with
DV profile 8.1 RPUs (`dovi_tool generate` + `inject-rpu`) and per-frame HDR10+ (`hdr10plus_tool
inject`); with the flag every frame's metadata survived (1137/1137 for each), without it none.
It stays contracted to never throw and never to touch the encoded file unless the whole
round-trip succeeded (temp file + atomic move). Keep the "experimental" label until it has been
checked against commercial DV / HDR10+ masters.

### Theming is additive

`MoistDark.axaml` is an overlay layered on top of `FluentTheme`, not a replacement. `App.ApplyTheme()` toggles by adding/removing that `Styles` object from `App.Styles`. A new theme means: `.axaml` in `Themes/`, `StyleInclude` in `App.axaml`, entry in `SettingsTabViewModel.ThemeOptions`, branch in `App.axaml.cs`.

## Code Conventions

- C# 14 / .NET 10, nullable enabled, implicit usings, file-scoped namespaces
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
| Add a video encoder | Settings class in `Core/Encoders/Video/` → `AppServices.RegisterVideoEncoder()` (ID must be `ffmpeg-{codec}`) → switch in `EncodeJobBuilder.CreateVideoSettings()` → codec name in `FFmpegVideoProcessor.GetFfmpegCodecName()` → `ContainerCompatibility.StreamCodecForEncoder()` and the container tables |
| Add a hardware encoder | One entry in `HardwareCapabilities.Known` (id must be `hw-<vendor>-<codec>`). Registration, detection, container validation and UI filtering all follow from it. A new *vendor* also needs rate-control and preset arms in `FFmpegHardwareVideoProcessor`. |
| Add a quality metric | `QualityMetric` enum + `QualityMetrics` metadata → filter name and parser in `QualityMeasurer` (remember `FrameSync`) → it becomes available to `compare`, `--measure` and target search automatically |
| Change chunk planning | `ChunkPlanner` only — and keep `ChunkPlan.Validate()` passing, since it's the guard against silently losing footage |
| Change per-scene behaviour | `PerSceneOptimizer`. Per-chunk CRFs feed the chunk directory fingerprint, so a run at a different target can't reuse the previous run's chunks — keep that true |
| Add an audio encoder | Settings class in `Core/Encoders/Audio/` → `AppServices.RegisterAudioEncoder()` (ID must be `ffmpeg-audio-{codec}`) → `FFmpegAudioProcessor.GetCodecSpecificArgs()` + `GetFfmpegCodecName()` → `EncodeJobBuilder.CreateAudioSettings()` + `AudioFileExtension()` → `ContainerCompatibility` |
| Add an encode option | `EncodeRequest` first, then `EncodeJobBuilder`, then surface it in *both* `EncodeTabViewModel.BuildRequest()` and `EncodeOptions.Build()` + `Help.cs` |
| Add a CLI command | Dispatch word in the `Program.Main` switch (`Program.cs`) + a section in `Help.cs`. A small command handler goes in `Commands.cs` (`encode`, `batch`, `probe`, `list`, `tools`); a substantial one gets its own file (`compare`/`crf-search` → `QualityCommands.cs`, `watch`/`health` → `WatchCommands.cs`, `serve` → `ServeCommand.cs`). Anything it encodes must still go through `EncodeJobBuilder`. |
| Test encoder arguments | `Core.Tests/AudioProcessorTests.cs` — subclass the processor, call `BuildArguments()`, assert on the string. No process launch. |
| Test real encoding | `Moist.IntegrationTests` — use `[FfmpegFact]`/`[FfmpegTheory]` so it skips without FFmpeg, and assert on the **probed** output, not the exit code |
| Add a filter | `FilterDefinition` in the engine's `GetAvailableFilters()` + the emit branch in its `GenerateScript()` |
| Add a filter engine | Implement `IFilterEngine`, register in `AppServices.RegisterDefaultFilters()` — but see the FFmpeg-only constraint above |
| Add a built-in profile | `EncodingProfile.GetBuiltInProfiles()` (won't apply to existing installs) |
| Add a setting | `Core/Settings/MoistSettings.cs` + `SettingsTabViewModel` (load in ctor, write back in the save path). Must be JSON-representable. |
| Add a tool to auto-discovery | `ToolDiscovery.KnownTools` + `AppServices.GetConfiguredToolPath`/`SetConfiguredToolPath` |

## Data & Runtime Paths

All under `%AppData%/moist` (Windows) / `~/.config/moist` (Linux/macOS), resolved **only** through `MoistPaths` (`Moist.Core/Settings`) — never call `GetFolderPath(ApplicationData)` directly. On Unix that returns `""` when `~/.config` doesn't exist yet (fresh account, CI runner, service user), and `Path.Combine("", "moist")` is the *relative* path `moist` — beside the published CLI, that's the executable itself, so every write failed with "The file '…/moist' already exists". `MoistPaths` creates the folder, falls back through `$XDG_CONFIG_HOME` → `$HOME/.config` → local app data → temp, always returns an absolute path, and honours a `MOIST_CONFIG_DIR` override:

- `settings.json` — app settings. Corrupt settings are deleted and defaults reloaded on startup failure.
- `profiles/*.json` — one file per profile, built-ins included
- `plugins/*.dll` — scanned recursively at startup; `IPlugin` types with a parameterless ctor are instantiated and given `Register(EncoderRegistry, JobQueue)`. Load failures are logged and skipped.
- `tools/` — auto-downloaded tool binaries
- `logs/moist_<timestamp>.log` — per-session; last 10 kept. **Everything the app logs** lands here (`FileLoggerProvider` in `Moist.Core/Diagnostics`, registered beside the console provider in `AppServices`), plus completed/failed job output via `AppServices.WriteJobLogToFile`. It used to receive only the job output, so a failure outside a queued job — a preview, a probe, a tool download — left no trace once the console scrolled (and the GUI has no console).
- `logs/crash_<timestamp>.log` — one per unhandled exception or unobserved task fault, written by `CrashLog` (`Moist.Core/Diagnostics`), which both entry points install first thing in `Program.Main`. Deliberately independent of the logging pipeline so it still works when that has failed or isn't built yet.
- `queue.json` — written on close, consumed once on startup (see the persistence gap above)
- `watch-<hash>.json` — per-watched-folder record of already-handled files, so `moist watch` doesn't re-encode on restart

Temp encodes go to `Settings.TempDirectory`, default `%TEMP%/moist`.

The Docker image relocates this whole config root to `/config` by setting
`XDG_CONFIG_HOME` (the container's `SpecialFolder.ApplicationData`), and declares it a
volume so settings, profiles and the queue survive a restart.

## CI/CD

- `ci.yml` — push/PR to `main`. **Pull requests from forks run on GitHub-hosted runners** (and
  install FFmpeg there); only pushes and same-repo PRs use the self-hosted ones, because on a public
  repo a fork PR is arbitrary code that would otherwise run on a maintainer's machine. Builds Debug + Release and runs tests on **self-hosted** runners (group `Default`, labels `Windows`/`Linux`). `DOTNET_INSTALL_DIR` is pinned into the workspace because the runners are self-hosted. A non-blocking step reports whether FFmpeg is present; if it isn't, the integration suite skips and CI still passes — install FFmpeg on the runners to actually exercise it. macOS jobs exist but are commented out.
- `release.yml` — **started manually** (`gh workflow run release.yml --ref main -f version=1.0.0`), not by
  pushing a tag: the bogware organisation restricts tag creation to automation, so people can't push
  tags but github-actions[bot] can. The `prepare` job checks the version against `Directory.Build.props`
  and that `CHANGELOG.md` has a `## [x.y.z]` section (which becomes the release notes), the builds run from
  that one resolved commit, and the tag is created through the API only after every build succeeded. The
  Windows build runs on `windows-latest` because **SignPath
  Foundation** (code signing for OSS) only signs GitHub-hosted builds; signing turns on when the
  `SIGNPATH_ORGANIZATION_ID` variable and `SIGNPATH_API_TOKEN` secret exist (see `SECURITY.md`,
  `.signpath/artifact-configuration.xml`) and fails the release if a shipped exe isn't validly signed.
  Publishes `SHA256SUMS.txt`; third-party actions are pinned to commit SHAs. Self-contained single-file builds of both the GUI and the CLI for Windows x64 (ZIP, plus a framework-dependent GUI ZIP) and Linux x64 (tar.gz), then a GitHub release. CLI archives are named `moist-cli-*`. Tags containing `-` are marked prerelease.
- `Dockerfile` (repo root) — a two-stage image that publishes **`Moist.Cli` only** and runs `moist serve` as a non-root user on port 7788. The .NET 10 images are **Ubuntu 24.04**: its `ffmpeg` 6.1 has SVT-AV1 but **no libvmaf**, so `--target-vmaf` is unavailable in the stock image. The image also ships an `ubuntu` user at uid 1000 (with `sudo`/`adm` groups), which the Dockerfile deletes before creating `moist` at uid 1000 — renaming it would keep those groups — `moist tools` reports what the build actually has. The version is stamped once for the whole solution from `src/Directory.Build.props` (`<Version>`), not per-project.
