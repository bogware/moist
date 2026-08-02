# moist — Roadmap

Where moist is going and why. Tiers are ordered by dependency, not by excitement:
each one is built on the one above it.

**Status key:** ✅ done · 🚧 in progress · ⬜ planned · 💭 under consideration

---

## Positioning

The video encoding landscape splits into two camps that don't overlap:

| | Strength | Weakness |
|---|---|---|
| **HandBrake** | Approachable, presets, cross-platform | Shallow control, no quality targeting |
| **StaxRip** | Very deep control | Windows-only, expects expertise |
| **Shutter Encoder** | Broadcast workflows | Narrow focus |
| **av1an / ab-av1** | Chunked parallel encoding, target-VMAF | CLI only, no GUI, AV1-centric |
| **Tdarr / Unmanic** | Library automation at scale | Not an encoder UI; server-oriented |

**The gap: nobody offers av1an/ab-av1-class quality intelligence behind a good
cross-platform GUI *and* a scriptable CLI.**

That is what moist is aiming at. It already has the pieces that make it possible —
a dependency-aware job queue, a filter engine, two front-ends over one shared
pipeline (`EncodeJobBuilder`), and profiles.

A second direction — library automation, competing with Tdarr — is deliberately
**not** being pursued. It pulls the architecture in a different direction and the
space is well served.

---

## Tier 0 — Correctness ✅

Not glamorous. The difference between a toy and a tool you'd trust with a library.
Everything above this tier is built on sand until it's done.

| Item | Status | Notes |
|---|---|---|
| Settings could never be saved; app hung on exit | ✅ | `WindowX/Y` defaulted to `double.NaN`, which `System.Text.Json` refuses to serialize. Every `Save()` threw; the unguarded call in `OnClosing` left the process hung. |
| Two-pass encoding completely broken | ✅ | Pass 1 wrote to `NUL`//dev/null without `-f null`, so FFmpeg couldn't infer a muxer. Cross-platform, not Windows-only. |
| AVI output always failed | ✅ | Matroska stores H.264/H.265 length-prefixed; AVI/TS need Annex-B. Fixed with `h264_mp4toannexb`/`hevc_mp4toannexb`. |
| No codec/container validation | ✅ | Doomed combinations burned a full encode before failing at mux. `ContainerCompatibility` now gates up front. |
| **10-bit sources fail entirely** | ✅ | `x264`/`x265` hardcoded 8-bit-only profiles (`high`/`main`). Any 10-bit input — HDR, anime, Blu-ray remuxes, ProRes masters — died instantly. |
| **Only one audio track ever encoded** | ✅ | Multi-language and commentary tracks were silently destroyed. |
| **Chapters silently dropped** | ✅ | The mux reads from temp files, so source chapters never survived. |
| **Attachments silently dropped** | ✅ | Subtitle fonts lost, so styled ASS rendered wrong. |
| **Silent sources failed to encode** | ✅ | Every encode built an audio job, so a video-only file — screen recordings, animation renders, GoPro clips — died on `-map 0:a:0`. |

### Why these survived so long

The unit suite never launched an external process or wrote a settings file. Every
one of these bugs is invisible to a test that doesn't actually encode something.
That's why `Moist.IntegrationTests` exists and why each fix above has a regression
test pinned to it.

### Notes on the fixes

- **Bit depth** is now opt-in rather than assumed. `-profile:v` is only sent when
  the user asks for one, because deriving it means guessing the encoder's internal
  bit depth and guessing wrong is fatal. `VideoBitDepth.Auto` follows the source.
- **HDR10 static metadata** (BT.2020 primaries, PQ transfer, mastering display,
  content light level) was verified to survive a re-encode once the profile is
  correct — FFmpeg propagates it. Dynamic metadata still doesn't; see Tier 6.
- **Chapters and attachments** are recovered by adding the source as an extra mux
  input purely to copy from. Attachments are Matroska-only, and chapters are
  skipped for containers that can't hold them.

---

## Tier 1 — Hardware encoding ✅

Table stakes in 2026; HandBrake, StaxRip and Tdarr all have it. 10–20× faster than
software x265 for the same wall-clock budget.

| Item | Status | Notes |
|---|---|---|
| NVENC (H.264 / HEVC / AV1) | ✅ | Tested on RTX 4080 |
| QSV (H.264 / HEVC / AV1 / VP9) | ✅ | Implemented, not verified on hardware |
| AMF (H.264 / HEVC / AV1) | ✅ | Implemented, not verified on hardware |
| VideoToolbox (H.264 / HEVC) | ✅ | Implemented, not verified on hardware |
| VAAPI (H.264 / HEVC / AV1 / VP9) | ✅ | Implemented, not verified on hardware |
| Runtime capability detection | ✅ | Probes the FFmpeg build *and* attempts a real encode — a listed encoder isn't a working one |
| Per-vendor rate control | ✅ | Hardware encoders don't accept `-crf`; each family has its own quality control |
| Native two-pass | ✅ | Hardware encoders don't understand `-pass`; NVENC uses `-multipass` |
| GUI + CLI exposure | ✅ | Unavailable encoders are hidden rather than offered and failed |

### Measured on an RTX 4080

40 seconds of 1080p, HEVC, CRF 26, preset slow:

| | Time | Size | VMAF |
|---|---|---|---|
| NVENC (`hw-nvenc-hevc`) | **4.9 s** | 37.5 MB | 74.16 |
| x265 (`ffmpeg-x265`) | 85.1 s | 29.2 MB | 74.10 |

**17× faster at the same perceptual quality, for a 28% larger file.** That is the
trade in a sentence, and it is why hardware is not simply "better".

### Design notes

- **Listing is not availability.** `ffmpeg -encoders` reports everything the binary
  was compiled with, so a stock Windows build advertises NVENC, QSV, AMF *and*
  VA-API regardless of the installed GPU. Detection encodes a few frames and keeps
  only what actually succeeds. Unavailable encoders are hidden, and the reason is
  reported — "DLL amfrt64.dll failed to open" beats a silent absence.
- **Two-pass is different.** Hardware encoders have no `-pass`; NVENC does its
  analysis inside a single run via `-multipass`. `TwoPassProcessor` explicitly
  refuses hardware jobs so it can't inject flags they reject.
- **Rate control is per-vendor.** None of them accept `-crf`. NVENC uses
  `-rc vbr -cq`, Quick Sync `-global_quality`, AMF `-rc cqp -qp_i/-qp_p`,
  VideoToolbox an inverted 0-100 `-q:v`, VA-API `-rc_mode CQP -qp`.
- **Recommended use:** previews and bulk work, with software encoders for final
  masters.

### TODO: verify the other four vendors

Only **NVENC** has been run against real silicon. Quick Sync, AMF, VideoToolbox and
VA-API are implemented from their documented interfaces and are correctly *detected*
as absent — but "correctly detected as missing" is not "known to produce a good file".
They are labelled `[experimental]` in both front-ends until someone runs them.

To verify a vendor, on hardware that has it:

```
moist list hardware                      # confirm it probes as usable
moist encode clip.mkv --video-encoder hw-<vendor>-hevc --crf 26 --measure
```

The encode must succeed, the output must probe as the expected codec, and the VMAF
score should land in the same region as a software encode at the same CRF. Once
confirmed, drop that vendor from `HardwareEncoderDefinition.Experimental`.

---

## Tier 2 — Quality metrics engine ✅

Measurement as a first-class feature, and the substrate Tier 3 is built on.

| Item | Status | Notes |
|---|---|---|
| VMAF | ✅ | With pooled min / max / mean / harmonic mean from libvmaf's JSON log |
| XPSNR | ✅ | Per-plane, headline figure weights luma 4:1:1 |
| SSIM / PSNR | ✅ | Cheap, familiar sanity checks |
| `moist compare a.mkv b.mkv` | ✅ | Any metric, `--json` for scripting |
| `--measure` on encode | ✅ | Scores the finished file against its source |
| Resolution handling | ✅ | A downscaled encode is scaled back up before comparing |
| 4K model selection | ✅ | VMAF's default model is trained for 1080p viewing |
| Per-frame score plot | 💭 | Find the worst scenes, not just the average |

No new dependencies: `libvmaf`, `xpsnr`, `ssim` and `psnr` all ship in stock FFmpeg
builds.

### The bug that nearly invalidated everything

Every one of these filters is built on FFmpeg's **framesync**, which by default keeps
going after the shorter input ends, holding its last frame. Comparing a 5-second
sample against a 40-second source therefore scored a *frozen frame against moving
video* for the remaining 35 seconds. One near-lossless clip measured **31** instead
of 98.

`shortest=1:repeatlast=0` is mandatory on every metric filter. There is a regression
test pinning it.

A second trap worth recording: **`testsrc2` is useless for quality work.** It is
synthetic high-frequency noise, and VMAF scores it into a narrow band (~71-75)
regardless of CRF, which makes it look as though quality doesn't respond to settings
at all. The integration tests use mandelbrot instead, which behaves like real footage:

| CRF | 14 | 24 | 32 | 40 |
|---|---|---|---|---|
| VMAF | 97.5 | 88.8 | 69.6 | 41.6 |

---

## Tier 3 — Target-quality encoding ✅

**The flagship feature.** Say what quality you want, not what CRF you guess.

| Item | Status | Notes |
|---|---|---|
| Sample-based CRF search | ✅ | Encodes a few short spans, not the whole file |
| Interpolated bisection | ✅ | Fits a line through the bracketing probes and jumps to the crossing |
| `--target-vmaf 95` on encode | ✅ | Searches, then encodes with the result |
| `moist crf-search` | ✅ | Search alone, for scripting |
| GUI "Find CRF" button | ✅ | Applies the result to the quality slider |
| Result caching | ✅ | Keyed on file identity, encoder, preset and target |
| Size estimate | ✅ | Reported as a share of the source |
| Hardware-accelerated search | ❌ | Deliberately rejected — see below |

### How well does sampling actually predict?

On a 720p clip, searching with three 5-second samples:

| | Predicted from samples | Actual full encode |
|---|---|---|
| VMAF at chosen CRF 20 | 93.39 | **93.46** |
| Size vs source | 64% | 61% |

A second run targeting VMAF 95 picked CRF 18, predicted 95.03, and the full encode
measured **95.06**. Sampling works.

### Why search doesn't use a faster proxy encoder

It's tempting to probe with NVENC and encode with x265 — the search would be ~17×
cheaper. It was rejected: the CRF that reaches VMAF 95 on one encoder does not reach
it on another, so the result would need a per-encoder calibration offset that is
itself content-dependent. That trades a correct answer for a fast wrong one.

Sampling already supplies the speed. Search always uses the encoder and preset the
real encode will use, which is correct by construction.

---

## Tier 4 — Chunked parallel encoding ✅

Scene-split → encode chunks in parallel → concatenate → **verify**.

| Item | Status | Notes |
|---|---|---|
| Scene detection | ✅ | FFmpeg's `scdet`; falls back to fixed chunks if it fails |
| Chunk planning | ✅ | Min/max duration bounds, tiny tails folded away |
| Parallel chunk encoding | ✅ | Runs through the existing job queue and its concurrency limit |
| Concatenation | ✅ | Stream copy, no re-encode |
| Frame-exact verification | ✅ | The joined result is counted, not trusted |
| Resume after interruption | ✅ | Finished chunks are reused; partial ones are discarded |
| GUI + CLI exposure | ✅ | `--chunked`, or the Chunking row in the encode tab |

### Frames, not seconds

Chunks are addressed in **frames**. At 23.976 fps a duration in seconds cannot
address a frame boundary, and the rounding error accumulates across chunks until
the joined file has gained or lost frames. Each chunk is encoded with
`-frames:v <count>`, which cannot drift, and `VideoJob.TrimFrames` exists for
exactly this.

### Verification is not optional

FFmpeg will happily exit zero having written a file that is a few frames short.
The concat step counts the frames in the result and fails the job when they don't
match the plan — a test deliberately truncates a chunk to prove it's caught.
Resume applies the same standard: a chunk file is only reused if it decodes to
exactly the frames expected, so a half-written file left by a kill is discarded
rather than spliced in.

Verified on a 40-second 1080p clip: **1200 frames in, 1200 out**, VMAF 60.15
chunked against 60.33 single-pass, output 0.4% larger from the extra keyframe per
boundary.

### How much faster is it, really?

Less than the marketing would suggest, and it's worth being straight about why.
Chunking can only recover the CPU your encoder *wasn't already using*. Measured on
a 16-core machine:

| | Result |
|---|---|
| 4 × x265 `slow` in parallel vs serially | **1.23x** — so x265 already saturates ~80% of the box |
| Chunked x265 `slow` vs single-pass | **1.19x** — matching that headroom almost exactly |
| Chunked SVT-AV1 preset 6 vs single-pass | **0.86x** — *slower*; SVT-AV1 threads well, so splitting only adds overhead |

So the implementation is extracting essentially all the available headroom; the
ceiling is the encoder's own threading. Chunking earns its place when:

- the encoder threads poorly (libaom, some x264 configurations),
- the core count is high relative to what one encoder instance can use,
- or you want an interrupted encode to resume — which is worth it regardless of
  speed, and is why this stays useful even at 1.2x.

It is **off by default** for that reason.

---

## Tier 5 — Per-scene optimization ✅

Tier 3 + Tier 4 combined: every scene gets its own CRF so perceptual quality is
uniform across the file instead of uniform in *bitrate*.

This is [Netflix's Dynamic Optimizer](https://netflixtechblog.com/dynamic-optimizer-a-perceptual-video-encoding-optimization-framework-e19f1e3a277f)
approach — 10–15% bitrate savings over per-title at the same quality. No consumer
tool offers it behind a GUI.

| Item | Status | Notes |
|---|---|---|
| Per-scene CRF assignment | ✅ | Each chunk carries its own quality through to the encoder |
| Cheap complexity measure | ✅ | Bytes per frame at a fixed CRF — how much is actually going on |
| Calibrate + model | ✅ | Search a few scenes spanning the range, fit CRF against log complexity |
| **Verify the model's work** | ✅ | Predictions are measured and re-searched when they miss |
| Full per-scene search | ✅ | `--per-scene-full`, for when accuracy beats cost |
| GUI + CLI exposure | ✅ | `--per-scene`, or the Per-scene checkbox |

### What it actually buys you

Measured on a clip whose four scenes span a ~400× complexity range:

| | worst scene | mean VMAF | size |
|---|---|---|---|
| fixed CRF 25 | **83.55** | 93.40 | 1,557 KB |
| fixed CRF 17 | 93.82 | 96.89 | 3,089 KB |
| **per-scene** | 93.80 | 94.78 | **2,629 KB** |

How this is framed matters. Against fixed CRF 25 the per-scene file is *bigger* —
but that CRF 25 average of 93.4 hides a scene sitting at 83.6, which is the one a
viewer would notice. A careful user picks a CRF low enough for the hardest scene,
and against **that** — equal worst-case quality — per-scene is **15% smaller**,
squarely in the range Netflix reported.

Quality spread across scenes fell from **13.9 VMAF to 3.6**. That is the actual
product: a single CRF gives every scene the same *effort*, not the same quality.

### The trap: compressibility is not perceptual tolerance

The first working version got this wrong in an instructive way. Complexity is
measured as "bytes at a fixed CRF", and a SMPTE bars pattern compresses to almost
nothing — so the model handed it CRF 42. It scored **80** against a target of 93,
because razor-sharp edges are exactly what a perceptual metric punishes when
quantised hard.

So modelled predictions are now **measured and re-searched when they miss**: one
cheap check per scene when the model is right, a real search only where it was
wrong. The same "verify, don't trust" stance as the chunk frame counts.

---

## Tier 6 — Professional / archival ✅ (mostly)

| Item | Status | Notes |
|---|---|---|
| HDR10 static metadata | ✅ | Survives a re-encode once the bit depth is right |
| HDR detection | ✅ | Reported by `probe`, and drives the warnings below |
| HDR-aware warnings | ✅ | Forcing 8-bit, tone-mapping, or losing dynamic metadata all say so first |
| Subtitle burn-in | ✅ | Verified by measuring the picture, not by exit code |
| Per-track subtitle control | ✅ | `--subtitle-track`, repeatable |
| 4:2:2 / 4:4:4 chroma | ✅ | Combines with bit depth; asking for chroma alone keeps the source's depth |
| SVT-AV1 tuning | ✅ | `--av1-tune vq` and friends |
| HDR10+ / Dolby Vision passthrough | ⬜ | Detected and warned about; the extract-and-reinject pipeline is not built |

### HDR detection can't trust the colour tags

Matroska doesn't necessarily surface `color_transfer` at container level — the HDR10
fixture here reports it as `unknown` while the bitstream is plainly PQ. Detection
therefore also reads frame **side data**, which is where mastering display, content
light level, HDR10+ and Dolby Vision actually live. Reading only `-show_streams`
misses every one of them.

### What's deliberately not done

The **HDR10+ / Dolby Vision passthrough pipeline** — extracting the RPU with
[`dovi_tool`](https://github.com/quietvoid/dovi_tool) or `hdr10plus_tool`, encoding,
then re-injecting — is not implemented. Neither tool is installed on the development
machine, so the code could be written but never run, and untested binary-metadata
surgery is worse than none.

What *is* done is everything that can be verified: both tools are known to tool
discovery so their absence is reported, dynamic metadata is detected in the source,
and the user is told plainly that a re-encode will drop it and that remuxing preserves
it. Finishing the passthrough needs the tools and some real Dolby Vision content.

---

## Tier 7 — Scale & automation ✅

| Item | Status | Notes |
|---|---|---|
| Watch folders | ✅ | `moist watch <dir> --output-dir <dir>` |
| Library health checking | ✅ | `moist health <dir> [--deep]` |
| Headless server mode | ✅ | `moist serve`, authenticated by default |
| Docker | ✅ | Built and run, not just written |
| Distributed workers | ⬜ | Not planned; see the positioning note |

### Watch folders

`moist watch` takes every option `moist encode` does and applies it to each file,
because a watch folder that encoded differently from the command that configured
it would be its own source of surprises.

Three things it has to get right, none of them obvious:

- **A file that has just appeared is usually still being copied.** It has a valid
  header and a plausible duration the whole way through, so encoding one early
  produces a truncated result with nothing to explain it. Files are only picked
  up once their size and timestamp have held still (`--stable-for`, default 30s).
- **Writing output into the watched folder feeds the watcher its own output** —
  it encodes the encode, then encodes that, until the disk fills. Refused up
  front, along with a `--move-to` inside a recursive watch.
- **What has been encoded is remembered between runs**, so restarting doesn't
  re-encode the folder; a file that *changes* afterwards is new work again.
  Sources are only deleted or moved after the encode actually succeeded.

Polling, not `FileSystemWatcher`: watch folders are usually network shares or
mounted volumes, where change notifications are unreliable or absent, and a
dropped event means a file is silently never encoded.

### Library health checking

`moist health` reports files that are damaged or unplayable, and exits non-zero
if any are, so it can drive a scheduled check.

The distinction that makes it worth having is depth. A header scan catches a
file whose write was interrupted. It does **not** catch damage: a file with a
corrupt middle has a perfectly good header and reports a plausible duration.
Measured on a deliberately corrupted fixture — the quick scan calls it healthy,
`--deep` reports both decode errors and that it decoded 85 of the 180 frames the
container claims. There is a test pinning each half of that.

Finding the hang this had is worth recording: `--deep` runs ffprobe to count
frames, and the code drained only stdout while stderr was redirected. A damaged
file makes ffprobe flood stderr, which fills the pipe buffer and deadlocks — so
the scanner hung on precisely the files it exists to find, and only on those.

### Headless server

`moist serve` puts an HTTP API over the same queue: health, tool discovery, probe,
submit an encode, list the queue, start and stop it.

An encode names its own input and output paths and can carry custom encoder
arguments, so **anyone who can reach the API can read and write files as the user
running it**. It therefore authenticates by default, binds to loopback by default,
and refuses to do neither — `--no-auth` is rejected on any non-loopback address. A
token is generated and printed rather than defaulted, and compared in fixed time.

Submitted options are turned back into a command line and parsed by the same code
the CLI uses, so `{"crf": 22}` means exactly what `--crf 22` means. There is one
definition of an option rather than two that drift.

`HttpListener` rather than ASP.NET Core: it is in the base library, so the
self-contained binary stays a single file and gains no dependencies for seven
endpoints.

### Docker

Built and run, not merely written — which is how both of its bugs were found. The
`/config` volume is created by Docker as root while the container runs as uid 1000,
so moist could not write its own settings; and the entrypoint hard-coded `serve`,
so `docker run moist tools` silently started a server instead. Both fixed, then
verified by encoding a file through the API in the container and checking the
output landed on the host volume.

Worth knowing: Debian's FFmpeg (5.1 in the image) has x264, x265 and VP9 but
**not** libsvtav1 or libvmaf, so AV1 encoding and `--target-vmaf` do not work in
the stock image. `moist tools` reports what was actually found.

---

## Queue persistence ✅

Queued encodes survive a crash or a closed window, and come back on the next launch.

The previous attempt stored a flattened snapshot of each job — id, name, input, output —
which cannot be turned back into anything runnable, because the encoder settings weren't
in it. That is why it was never wired up: restoring was structurally impossible, not
merely unfinished.

What is stored instead is the `EncodeRequest`, since `EncodeJobBuilder.Build` turns one
deterministically back into the whole job graph. Chunked encodes recover better still:
the chunk directory is keyed on the same settings, so a restored run picks up every
chunk the previous one finished.

Restored encodes are **queued, not started**. Coming back to a machine already grinding
through work nobody asked for would be worse than losing the queue.

## Standalone x264 / x265 ✅

Both had never been run. Neither worked, and the reasons were worth writing down.

**Every encode failed, with an empty error message.** These encoders read raw frames on
stdin, so moist pipes FFmpeg into them through a shell — and `cmd.exe` strips the first
and last quote of whatever follows `/c`. A pipeline starts with a quoted tool path, so
`/c "ffmpeg" -i in | "x264" -o out` reached the parser as `ffmpeg" -i in | "x264`. What
failed was the mangled first token, not the encoder, which is why nothing useful was
reported. Both processors carried their own copy of the bug; the shell handling now
lives in one place, `ShellPipeline`.

**The command in the log was never the command that ran.** It was written before the
processors replaced it, so the one artefact you reach for when a job fails was fiction.
Now logged after.

**x264's arguments were built twice, by two methods that had drifted.** The version that
ran dropped reference frames, B-frames, CABAC, threads and Blu-ray compatibility.

**10-bit was silently truncated.** x264 and x265 both default their output to 8-bit
regardless of what the pipe carries. Measured: a 10-bit source through x264 came out
`yuv420p` — the master's extra precision gone, exit code 0.

**Trims were ignored entirely.** Chunked encoding, target-quality search and per-scene
optimisation all work by encoding *pieces* of the source. These encoders encoded the
whole file every time — so every chunk and every quality probe was wrong.

**x265 has no muxer at all** — it writes a raw HEVC elementary stream, which carries no
timing whatsoever. Anything reading it afterwards guesses the frame rate, and FFmpeg
guesses 25: the video changes duration and slides out of sync with its own audio. It is
now wrapped where the source rate is still known. Two traps found while doing it: on
FFmpeg 8 the raw demuxer emits packets with *no* timestamps and Matroska rejects them
outright, so timestamps are synthesised with the `setts` bitstream filter; and muxing to
MP4 instead "succeeds" while quietly dropping frames (178 of 180 in the test).

Verified against 8-bit, 10-bit and 24000/1001 sources, plus a filename containing `&`,
spaces and non-ASCII characters — the pipeline exposes paths to shell parsing twice.

## Known gaps not yet tiered
- **HDR10+ / Dolby Vision passthrough.** Detected and warned about, but not preserved.
  Needs `dovi_tool` / `hdr10plus_tool` and real Dolby Vision content to build against.
- **VapourSynth and AviSynth chains cannot be encoded.** The engines generate correct
  scripts, but nothing runs them: `FilterEngineId` is carried on the job and read by no
  one, so a chain would reach FFmpeg as a `-vf` string and be rejected. The GUI blocks it
  and the CLI can only select the FFmpeg engine, so nothing produces a wrong file — the
  feature is simply absent. Wiring it up means piping `vspipe`/`avs2pipe` into the
  standalone encoders (now that those work, this is tractable) *and* settling the source
  filter: the generated script calls `lsmas.LWLibavSource`, which is a third-party plugin
  that ships with neither VapourSynth nor moist. Left unbuilt deliberately — VapourSynth
  isn't installed here, and an unverified encode path is exactly what this section is
  full of.
