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

## Tier 5 — Per-scene optimization ⬜  ← next

Tier 3 + Tier 4 combined: every scene gets its own CRF so perceptual quality is
uniform across the file instead of uniform in *bitrate*.

This is [Netflix's Dynamic Optimizer](https://netflixtechblog.com/dynamic-optimizer-a-perceptual-video-encoding-optimization-framework-e19f1e3a277f)
approach — 10–15% bitrate savings over per-title at the same quality. No consumer
tool offers it behind a GUI.

Both halves now exist: Tier 4 already splits the file at scene boundaries and
encodes the pieces independently, and Tier 3 already finds the CRF that reaches a
target on a sample. Per-scene optimisation is running the second over each of the
first rather than over the file as a whole.

Note this makes chunking pay for itself even at 1.2x wall-clock: a per-scene
encode has to be chunked anyway, so the parallelism comes free with the quality
win.

---

## Tier 6 — Professional / archival ⬜

| Item | Status | Notes |
|---|---|---|
| HDR10 static metadata | ✅ | Verified: survives a re-encode once the bit-depth fix is in place |
| HDR10+ dynamic metadata | ⬜ | Needs `hdr10plus_tool`; lost on re-encode today |
| Dolby Vision RPU | ⬜ | Needs [`dovi_tool`](https://github.com/quietvoid/dovi_tool) |
| HDR-aware UI | ⬜ | Detect HDR input, warn before tone-mapping it away |
| Subtitle burn-in | ⬜ | Hard-sub for devices that can't render |
| Per-track subtitle control | ⬜ | Currently all-or-nothing |
| 4:2:2 / 4:4:4 chroma | ⬜ | Bit-depth work laid the groundwork |
| SVT-AV1 `--tune vq` | ⬜ | [SVT-AV1 4.0](https://wiki.x266.mov/docs/encoders/SVT-AV1) merged the PSY perceptual work into mainline; moist doesn't expose it |

---

## Tier 7 — Scale & automation 💭

Deliberately deprioritised — this is Tdarr's territory and a different product.
Revisit only if the quality-intelligence direction is complete.

Watch folders · distributed workers · REST API · headless server mode · Docker ·
library health checking.

---

## Known gaps not yet tiered

- **Queue persistence is half-wired.** `JobPersistence.Save()`/`Load()` both run, but
  `RestorePersistedJobs()` only reports a count and never re-enqueues, and `Load()`
  deletes the file. Crash recovery is effectively absent.
- **Tool discovery runs twice at startup.**
- **Settings prerequisites list has overlapping text** (`OKFFmpeg`).
- **x264/x265 CLI, VapourSynth and AviSynth paths are untested** — those tools
  aren't installed on the development machine.
