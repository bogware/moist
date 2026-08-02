# Changelog

All notable changes to moist are recorded here.

This project follows [Semantic Versioning](https://semver.org/). Until 1.0 the
minor version carries breaking changes.

---

## [0.9.0] — 2026-08-02

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
