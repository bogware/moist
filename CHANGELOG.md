# Changelog

All notable changes to moist are recorded here.

This project follows [Semantic Versioning](https://semver.org/). Until 1.0 the
minor version carries breaking changes.

---

## [Unreleased]

A release-hardening pass over the whole codebase (a full audit: 80 confirmed issues,
0 critical, all addressed) plus a set of enhancements. Build is clean and the full test
suite is green.

### Added

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
