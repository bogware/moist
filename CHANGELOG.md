# Changelog

All notable changes to moist are recorded here.

This project follows [Semantic Versioning](https://semver.org/). From 1.0 onwards,
breaking changes bump the major version.

---

## [Unreleased]

### Changed

- **Moved to .NET 10.** Every project targets `net10.0` (C# 14), the Microsoft.Extensions logging
  packages follow at 10.0.15, and CI, the release workflow and the Docker image build on .NET 10. The
  Docker image is now based on Ubuntu 24.04 (the .NET 10 default), so its FFmpeg gains SVT-AV1. The
  image's stock `ubuntu` account (uid 1000, in the `sudo` and `adm` groups) is removed and the
  `moist` service user recreated at uid 1000 with no other groups, so existing volumes keep working.
- **A new front page.** The README is rewritten for production: no emojis, plain prose, and a
  showcase of the new film-restore profiles from a DVD-era source, with 1:1 detail crops.

### Added

- **Film Restore to 1080p (Real-ESRGAN)** and **Film Restore to 1080p (Classic)** — built-in
  profiles for maximum film-like fidelity. No denoiser (it removes grain and fine detail with the
  noise); a light de-block for compression damage only; contrast-adaptive sharpening; a measured
  contrast and colour lift; and fine film grain laid down last, encoded with x265 `tune=grain`. The
  Classic profile sharpens at the source resolution before a Lanczos upscale and again after it.
- **`grain` filter** — fine, temporal film grain on the luma (optional half-strength chroma).
- **Configurable finish after an AI upscale.** A profile's `AiFinishFilters` run on the
  Real-ESRGAN-enlarged frames, after the scale to the target height; without them the light default
  finish (CAS + deband) applies. Grain belongs there: the model would scrub any grain fed to it.
  The finish is part of the resume key.

### Fixed

- **The encoder tune was dropped on the Real-ESRGAN path**, so a `tune=grain` profile lost its grain
  exactly where it mattered. `-tune` is now passed for x264 and x265 there too, and is part of the
  resume key.

## [1.0.0] — 2026-11-13

The first stable release: a release-hardening pass over the whole codebase (a full audit:
80 confirmed issues, 0 critical, all addressed), a pre-publication security and privacy
review, and a set of enhancements.

### Breaking changes

- **`moist serve` on a non-loopback address now requires `--root <dir>`** (repeatable).
  Every input, output, external-subtitle and probe path must resolve under a root, and moist's
  configuration folder is always refused. The Docker image passes `--root /media`.
- **`moist serve` accepts an allow-list of encode options.** Tool paths (`ffmpeg`, `ffprobe`), raw
  encoder arguments (`video-args`, `audio-args`), `temp-dir`, `output-dir`, `suffix` and
  `filter-engine` are refused with a 400 that says why.
- **Standalone x265 (`cli-x265`) encodes without B-frames by default.** See *Fixed*; choose
  `--x265-wrap mkvmerge` (or Auto in the GUI) to keep B-frames.
- **Filter parameters given on the command line or over the API are validated** (ranges and
  choices, and free text may not contain `; [ ] ' " \``). Values the GUI could never produce now
  fail with a usage error instead of reaching the filter graph.

### Security

- **Remote code execution through `moist serve` closed.** A token holder could previously name
  the FFmpeg binary to run (`--ffmpeg`), append shell syntax to the standalone encoders' pipeline
  (`--video-args`), inject Python into a generated VapourSynth script (`vs_resize` kernel), open a
  second FFmpeg filtergraph through free-text filter values, and read or write any path the
  service user could. All are closed: option allow-list, `--root` confinement, filter validation,
  the VapourSynth kernel mapped from known names, and Windows `cmd` quoting that refuses `"`
  instead of mis-escaping it.
- **The serve token can come from `MOIST_TOKEN`**, and the Docker entrypoint no longer puts it on
  the command line, where any user on the host could read it from the process list.
- **Every tool download is verified.** SHA-256 hashes are now recorded for all auto-downloaded
  tools (FFmpeg, MKVToolNix, dovi_tool, hdr10plus_tool, Real-ESRGAN, RIFE), so a tampered or
  corrupted download is rejected before extraction. The Linux FFmpeg URL pointed at a BtbN daily
  build that had been deleted (404). It now pins a month-end build, which BtbN keeps.
- **CI no longer runs pull requests from forks on self-hosted runners**, which would execute
  untrusted code on the maintainers' machines once the repository is public. Fork PRs run on
  GitHub-hosted runners.
- **Release signing and checksums.** Windows binaries are signed through SignPath Foundation once
  the project is enrolled, and fail the release if a shipped executable isn't validly signed. Every
  release publishes `SHA256SUMS.txt`. Third-party actions are pinned to commit SHAs, and the tag
  name is no longer interpolated into release scripts.
- **Vulnerable dependencies updated.** Tmds.DBus.Protocol 0.21.2 → 0.21.3 (GHSA-xrw6-gwf8-vvr9,
  shipped in the Linux GUI) through Avalonia 11.3.23. Test-only System.Net.Http and
  System.Text.RegularExpressions 4.3.0 are gone with xunit 2.9.3.
- `SECURITY.md` describes how to report a vulnerability and the security model.

### Added

**A new front page.** The README was rewritten as a proper tour with real screenshots, a
before/after restoration comparison, an animated target-quality demo built from real CLI output, and
a candid account of what restoration can and can't do. The deep reference moved, intact and
corrected, into `docs/` (feature guide, command line, FAQ, development).

**Open a file from the command line or "Open with…"** — `Moist.App video.mkv` loads it into the
Encode tab exactly as dragging it onto the window would.

**Preview overhaul.** The Encode-tab preview panel is now **resizable** (drag the divider)
and shows **source and output together** — the output rendered through the *entire* pipeline
(filters, encoder, bit depth, chroma, AI upscale / RIFE), on demand, so it's a true
before/after rather than just the filter chain. A separate resizable **Full Preview** window
adds snippet scrubbing, a bigger side-by-side, and **Open in player** for motion.

**Every prerequisite is installable in-app.** Settings → Prerequisites now offers, for each
tool, one-click **Install** (auto-download) or **Get** (opens its download page), plus a
**Browse…** to locate an existing binary — and the list **re-scans automatically** after any
of them, so it's seamless. `dovi_tool` and `hdr10plus_tool` gained auto-download.

**Plugin discovery.** The Plugins tab browses a curated online catalog (search + engine /
category filters) and installs plugins directly (https-only download into the plugins folder),
or opens the plugin's page when there's no direct link.

**SVT-AV1 controls.** Film-grain synthesis, tile rows/columns and keyframe interval are now
exposed (`--av1-film-grain`, `--av1-tile-rows`, `--av1-tile-columns`, `--av1-keyint`, and in
the GUI). They also feed the chunk-resume fingerprint so a resume can't mix settings.

**Experimental Dolby Vision / HDR10+ passthrough.** Opt-in `--preserve-dynamic-hdr` (and a
GUI toggle) attempts to carry the source's dynamic HDR metadata across the re-encode via
`dovi_tool` / `hdr10plus_tool`. **Unverified** — it degrades to a warning and never corrupts
the output; always check the result plays correctly.

**Download integrity.** Auto-downloaded tools are now SHA-256 verified against pinned
release URLs; an unverified download is loudly flagged rather than trusted silently.

**GUI parity.** RIFE fps, AV1 tune, subtitle-track selection, allow-long AI upscale and
disable-metadata are now reachable from the GUI, matching the CLI.

**Telecine-aware filters.** `ivtc` recovers the original ~24 fps film frames from telecined
video — including broadcasts that were bob-deinterlaced to 60p (it weaves frame pairs back
to 30i first, then `fieldmatch,decimate`), reuniting the real fields with nothing interpolated:
judder-free and 2.5× fewer frames to encode. Also `mpdecimate` (generic duplicate-frame
dropping) and `grayscale` (strips broadcast chroma noise from B&W sources). The cadence
filters are flagged as incompatible with chunked encoding.

**Observability.** Everything the app logs now reaches the session log file (it used to get
only queued-job output, so a failing preview or probe left no trace), console verbosity no
longer throttles it, and any unhandled exception or unobserved task fault writes a
`crash_*.log`. Settings gained an **Open Log Folder** button.

**Faithful native-720p60 profiles for the same captures.** *Broadcast 720p60 Film - Faithful 720p (B&W)* and *(Colour)* keep the broadcast's own frame rate, size and grain: crop the bars, strip chroma on B&W, CAS 0.3, a gentle contrast curve (1.04×, -0.01 brightness, +5% saturation on colour), x265 CRF 17 10-bit with `tune=grain`, and the original AC3 passed through. Built after side-by-side review showed the upscale/denoise chain trading the film's texture and motion for smoothness.

**Built-in profiles for over-the-air captures of film-sourced TV.** *Broadcast 720p60 Film -
B&W to 1080p* and *- Colour to 1080p* turn a MeTV-style MPEG-2 1280×720p59.94 recording (24 fps film
bob-upconverted to 60p, 4:3 picture pillarboxed with 88 px black bars) into 1080p23.976 HEVC 10-bit:
crop the bars, `ivtc` the bobbed fields back to an exact film cadence, strip the chroma noise on the
B&W era, de-block, denoise, GPU neural upscale (FSRCNNX via libplacebo; spline without a Vulkan GPU),
CAS and deband. *Premium Real-ESRGAN* variants of both swap in the deep-model upscale — the sharpest
result of the three upscalers compared, at roughly thirty times the encode time. Tuned on Gunsmoke
captures from four seasons and judged frame-by-frame against spline; the recipe is in the README.

**Resiliency.** A stall watchdog kills an encode tool that goes silent (15 min, per
processor) and fails the job with a clear reason instead of hanging an unattended batch
forever. It covers every tool moist launches, including the frame-based Real-ESRGAN and RIFE
steps, which previously had no timeout at all; `ffprobe` analysis and the hardware-encoder
listing are bounded by timeouts; the chunk frame-counter drains stderr (a corrupt chunk
could deadlock it). The AI processors also log each step (extract/upscale/encode/concat,
window progress, resume reuse, tool failures with the output tail) to the session log, and
frame extraction — minutes on a long source — now reports a frame count instead of sitting
at 2%.

### Fixed

- **Opening the Filters tab could silently change your encode.** Every parameter got an editor
  of every type, with the unused ones merely hidden. A hidden decimal editor bound to an integer
  parameter started at 0, was clamped up to the parameter's minimum by its control, and wrote that
  back: loading the *AI Upscale SD to HD* profile and glancing at the Filters tab turned Deblock's
  strength 4 into 1 for every later encode. Only the editor matching the parameter's type can write
  it now.
- **A relative `--temp-dir` (or output) broke GPU super-resolution.** That filter runs FFmpeg in the
  shader folder so the shader resolves by bare name, and relative paths then pointed into it. Every
  path in a request is made absolute before any job is built.
- **`--dry-run` called real options unknown.** `--measure`, `--per-scene` and `--per-scene-full` are
  only read on paths a dry run never reaches, so checking a command first reported it as wrong.
- **Queue tab:** a completed mux showed an empty progress bar, finished jobs still offered *Cancel*,
  and the job log was buried in FFmpeg's machine-readable progress lines and scrolled sideways
  mid-word. All fixed; log files on disk still keep everything.
- **Settings → Prerequisites** showed a line of help text as the "version" of Real-ESRGAN and RIFE,
  which have no version flag. They now read *installed*.
- **Dolby Vision / HDR10+ passthrough never actually worked.** The metadata was extracted and
  re-injected correctly, but the enriched raw HEVC couldn't be put back in a container: FFmpeg 8
  refuses untimed packets, and libx265's B-frames rule out numbering them. The step failed safely
  every time, keeping the plain encode. It now remuxes with mkvmerge, timed from the encoded
  file's own frame duration, and is verified end-to-end on synthetic DV 8.1 and HDR10+ clips
  (every frame's metadata preserved). Its scratch files also moved from the system temp folder
  to moist's temp directory.
- **Chunked encoding failed on every broadcast capture.** MPEG-TS records no frame count, so the
  plan used duration × rate, and the container duration includes the audio's head start. A MeTV
  clip was planned as 4509 frames, decoded to 4462, and the join's verification refused the
  result. The frame count is now decoded when the container has none.
- **Frame counts of MPEG-TS files read as unknown.** ffprobe lists a TS stream twice (once under
  its program) with trailing commas. The parse rejected that, which silently disabled the
  decoded count above and made `health --deep` report 0 frames for every `.ts` file.
- **A batch could lose its temp folder mid-run.** Cleanup removed any directory it left empty,
  including the temp directory itself. The jobs a batch had already built then wrote into a
  folder that no longer existed. Only per-run `chunks/<hash>/` folders are removed now.
- `moist serve` help: the `queue/start` example now sends `-d ""`, since a bodiless POST gets
  *411 Length Required* from Windows' HTTP stack.
- **Standalone x265 output had scrambled timestamps.** x265 writes an untimed HEVC stream, and
  the FFmpeg wrap numbered its packets in decode order. With B-frames that put presentation
  timestamps out of order (0, .042, .167, .125, …). The frames decoded correctly, but players and
  remuxes that trust timestamps stuttered or dropped frames. On FFmpeg 5/6 the wrap was also off
  scale (2 s of video labelled 0.09 s). The new **stream wrap** setting (`--x265-wrap`, GUI "Wrap")
  offers: **No B-frames** (default, works everywhere, ~10–20% larger files), **mkvmerge** (keeps
  B-frames; MKVToolNix reads the picture order), and **Auto**. Every wrap is now verified
  (duration = frames ÷ rate), with a millisecond fallback for older FFmpeg. MKVToolNix
  auto-downloads on Windows (7z unpacked in-process, hash-verified).
- **Real-ESRGAN / RIFE frame rate after `ivtc` on FFmpeg 5.x/6.x.** Older FFmpeg's `decimate` drops one frame in five but leaves the survivors on the 30i timestamp grid, so the rate probe read 29.97 instead of 23.976 — on Debian 12 and the Docker image that would have played AI-upscaled film fast. The probe now detects uneven spacing, averages over 48 frames and snaps to a simple multiple of the source rate.
- **Stall watchdog on Linux/macOS.** A killed tool is now waited for (bounded) before the job fails, since `Kill` only signals on Unix; cleanup could otherwise race a tool that was still alive.
- **Configuration written to the working directory on Linux/macOS.** On an account without `~/.config` (fresh installs, CI runners, service users) the platform config folder resolved to an empty string and settings, logs and profiles went to a relative `moist/` folder — which collides with the `moist` executable and failed every write. All config paths now go through one resolver that creates the folder, falls back through `$XDG_CONFIG_HOME` and `$HOME/.config`, and accepts a `MOIST_CONFIG_DIR` override.
- **Audio/video drift after reception dropouts with `ivtc`.** A broadcast capture with signal
  dropouts carries timestamp gaps in both streams. `decimate` rewrote the video timestamps as if
  the input were continuous, so every gap vanished from the picture while the audio kept it; the
  picture led the sound by 1.8 s by the end of a one-hour episode. The `ivtc` filter now opens with
  `fps=source_fps`, which fills each gap with duplicate frames so a dropout is a brief freeze over
  silent audio, in sync, rather than a growing lead.
- **Real-ESRGAN / RIFE output speed after a cadence filter.** The frame-based AI processors
  encoded the extracted frames at the *source* frame rate, so an ivtc'd 59.94 broadcast capture
  came out as 23.976 frames played at 59.94 — 2.5× speed, 30 s of picture against 75 s of sound.
  The rate is now measured through the pre-clean chain (`settb=AVTB,showinfo` on a dozen frames)
  and snapped to the exact rational, so 23.976 is `24000/1001`, not a rounded decimal. The same
  processors also ignored trims, so a 3-second snippet preview upscaled the entire file and left a
  scratch the full encode would have "resumed" from; trims are now applied to extraction and are
  part of the scratch key.
- **Audio/video sync on broadcast captures and stream-copied cuts.** When a source's video
  starts later than its audio (the norm for DVR/OTA recordings — the first keyframe lands ~1 s
  after the first audio packet), the separately-encoded streams were muxed from 0 and the
  picture ran ahead of the sound by the whole stagger. Each stream's `start_time` is now read
  and every audio track is anchored to the first video frame (a lead is trimmed, a lag is
  delayed in the mux). Covered by a staggered-fixture integration test.
- **Subtitle muxing into MP4/MOV/WebM** no longer fails the whole mux — text subs transcode
  to `mov_text`/`webvtt`; subset selection maps only the chosen streams with correct
  language/forced/default; container-incompatible subs are dropped with an up-front warning.
- **Preview and probe helpers** (preview extraction, ffprobe/Real-ESRGAN/RIFE/quality) no
  longer deadlock on chatty or corrupt inputs — both output pipes are drained concurrently.
- **VP9 CRF** now enters true constant-quality mode (`-b:v 0`); was silently ~256 kbps.
- **Watch folders** survive an I/O error mid-scan, retry failed encodes (bounded), preserve
  the source subtree on recursive output, and never loop output back into the watch folder.
- **Encoder guards:** invalid tune/codec pairs (x264-only tunes on x265, non-psnr/ssim on
  vp9), and AI-upscale/RIFE combined with chunking, are caught before the encode instead of
  producing a wrong or failed result.
- **`moist serve`** hardened: header-only bearer token, DNS-rebinding `Host` guard and JSON
  content-type check on loopback, bounded request body, and bounded job retention.
- **Quality metrics** parse `inf`/`nan`, so a lossless frame no longer crashes the measurer.
- Audio `S24` sample format, Opus/MP3/Vorbis rate-control flags, theme toggle, cross-thread
  queue events, atomic profile writes, and many smaller correctness and resource fixes.

---

## [0.10.0] — 2026-08-08

The AI restoration release. moist grew a full ladder for bringing old SD video up to
sharp HD — from classical filters through GPU shaders to deep-model AI — plus the
tooling to make it one command.

### Added

**A one-click restoration pipeline.** Two built-in profiles — *Restore SD to HD
(vintage)* and *AI Upscale SD to HD (GPU)* — clean, upscale, sharpen and de-band a
vintage DVDRip in a single step. New filters back them: `deblock` (MPEG artifact
removal), `cas` (contrast-adaptive sharpen), and `superres` — **GPU neural upscaling**
(FSRCNNX / Anime4K) that runs as a libplacebo shader inside the same FFmpeg pass and
falls back to a spline scale where there's no Vulkan GPU.

**Real-ESRGAN deep-model upscaling.** `--ai-upscale` (and the *AI Upscale (Real-ESRGAN)*
profile) run the deep model frame-by-frame for the highest quality. Long sources stream
in windows — bounded scratch, resumable — so it handles full films, not just clips.

**RIFE AI frame interpolation** and **arbitrary frame-rate conversion.** `--fps <n>` with
`--fps-mode dup | motion | rife`: drop/duplicate, motion-compensated (`minterpolate`), or
AI-interpolated (RIFE) — change any source to any frame rate.

**VapourSynth is now encodable.** A job on the VapourSynth engine is piped
`vspipe → ffmpeg`, unlocking `vs_superres` (vs-mlrt ONNX / TensorRT / OpenVINO). Select it
with `--filter-engine vapoursynth`.

**Tools install themselves.** `moist tools install <id>` downloads, extracts and wires up
standalone tools (FFmpeg, Real-ESRGAN, RIFE) from the command line, not just the GUI.

**Configurable output.** Ask-where-to-save vs. a default folder, a configurable output-name
suffix, reveal-folder-when-done, and first-class temp/scratch and default-output directories.

### Fixed

- **The output file extension is honoured.** `-o out.mp4` used to write Matroska into a
  `.mp4` name; the container is now inferred from the extension, and a doomed codec/container
  pair is rejected up front instead of after a full encode.
- An ambiguous `--profile` prefix now errors with the matches instead of silently guessing.

### Known gaps

- **VapourSynth + vs-mlrt** are a Python + plugin ecosystem, so they're detected and guided,
  not auto-provisioned — install VapourSynth and drop vs-mlrt + an ONNX model into its plugins.
- Real-ESRGAN and RIFE are frame-based, so slow; NVENC-on-frames and a RIFE GUI control are
  future work.

---

## [0.9.1] — 2026-08-02

The first published release. (0.9.0 was tagged but never built: the test suite
assumed an FFmpeg with libsvtav1 and libvmaf compiled in, which most builds do
not have, so it failed on the release runners. No artifacts were published under
that tag.)

The first release worth using. moist began as a GUI that could not reliably
complete an encode; this release makes the encoding correct, adds a CLI that
drives the same pipeline, and builds quality-targeted encoding on top.

### Added

**A command-line interface.** `moist encode`, `probe`, `measure`, `queue`,
`tools` and `profiles`. The GUI and the CLI share one pipeline — a request goes
through `EncodeJobBuilder` and produces the same job graph either way — so the
two cannot drift apart in what they actually encode.

**Quality measurement.** VMAF, XPSNR, SSIM and PSNR, via `moist measure` or
`--measure` after an encode.

**Target-quality encoding.** `--target-vmaf 93` searches for the CRF that hits a
quality target, sampling the source rather than encoding it whole, then
interpolating between probes. Results are cached and keyed on the settings that
affect them.

**Chunked parallel encoding.** `--chunked` splits at scene boundaries and
encodes the pieces in parallel. Chunks are addressed in frames, never seconds,
and the joined result's frame count is verified against the source. Interrupted
runs resume from the chunks that finished.

**Per-scene optimisation.** `--per-scene` probes each scene's complexity, fits a
model, and gives each its own CRF. Predictions are *verified* and re-searched
where they miss — a complexity model alone will hand a flat colour field a CRF
far past the point it still looks acceptable.

**Hardware encoding.** NVENC, QSV, AMF, VideoToolbox and VAAPI, with per-vendor
rate control. Availability is established by running a real encode, because the
encoder being listed does not mean it works on this machine.

**10/12-bit and 4:2:2 / 4:4:4 output**, HDR10 metadata preservation, HDR-aware
warnings before anything is lost, subtitle burn-in, per-track subtitle and audio
selection, chapters, attachments and container metadata.

**Queue persistence.** Queued encodes survive a crash or a closed window and come
back queued — not running — on the next launch.

**Watch folders.** `moist watch <dir> --output-dir <dir>` encodes whatever lands
in a folder, using every option `moist encode` takes. Files are picked up only
once they have stopped changing, because one still being copied has a valid header
and produces a truncated encode with no error. What it has already done survives a
restart, and sources are only moved or deleted after the encode succeeded.

**Library health checking.** `moist health <dir>` finds damaged and unplayable
files and exits non-zero if there are any. `--deep` decodes everything rather than
reading headers, which is the only way to catch a file that is corrupt in the
middle — that has a perfectly good header and a plausible duration.

**A headless server.** `moist serve` exposes the queue over HTTP: submit encodes,
watch progress, probe files. Authenticated by default and bound to loopback by
default, because an encode names its own input and output paths.

**A Docker image.** `docker run -p 7788:7788 -v /media:/media moist`. Note that
Debian's FFmpeg has no libsvtav1 or libvmaf, so AV1 and VMAF targeting are
unavailable in the stock image; `moist tools` reports what is actually present.

### Fixed

Bugs that made ordinary encodes fail or silently produce the wrong file:

- **Settings could never be saved, and the app hung on exit.** `WindowX`/`WindowY`
  defaulted to `double.NaN`, which `System.Text.Json` refuses to serialize, so
  every save threw. The unguarded call while closing left the process hung.
- **Two-pass encoding never worked.** Pass 1 wrote to `NUL`//dev/null without
  `-f null`, so FFmpeg could not infer a muxer.
- **10-bit sources failed outright.** x264 and x265 hardcoded 8-bit-only
  profiles, so HDR, anime, Blu-ray remuxes and ProRes masters all died instantly.
- **AVI and TS output always failed.** Matroska stores H.264/H.265
  length-prefixed; those containers need Annex-B.
- **Only the first audio track was ever encoded.** Commentary and other languages
  were discarded without a word.
- **Chapters and attachments were dropped**, so styled subtitles rendered with
  the wrong fonts.
- **Silent sources could not be encoded at all** — every encode built an audio
  job, so screen recordings and animation renders died on `-map 0:a:0`.
- **The standalone x264/x265 encoders did not work at all**, failing with an
  empty error message: the shell mangled the pipeline before either tool saw it.
  They also truncated 10-bit to 8-bit, ignored trims (so every chunk and quality
  probe encoded the whole file), and in x265's case produced a raw stream with no
  timing, which plays at the wrong speed and drifts out of sync with its audio.
- **Doomed codec/container combinations** burned a full encode before failing at
  the mux; they are now rejected up front.

### Changed

- Job processors are created per job rather than shared, so concurrent encodes no
  longer race on the same progress counters.
- Temp files are kept when a job fails, so a failed encode can be examined and
  retried instead of vanishing.
- The command written to a job's log is now the command that actually ran.

### Known gaps

- HDR10+ and Dolby Vision dynamic metadata is detected and warned about, but not
  preserved through an encode.
- VapourSynth and AviSynth filter chains cannot be encoded; only the FFmpeg
  filter engine is wired into the pipeline. The GUI blocks the attempt rather
  than producing a broken file.
- QSV, AMF and VideoToolbox are implemented but have not been run on real
  hardware.

### Requirements

.NET 8, plus FFmpeg and FFprobe on `PATH`. Windows and Linux builds are
published; macOS builds from source.
