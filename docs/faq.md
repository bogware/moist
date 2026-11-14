# moist — FAQ, tips & troubleshooting

[← back to the README](../README.md) · [Features](features.md) · [Command line](cli.md) · [FAQ](faq.md) · [Development](development.md)

## Tips, Tricks & Hard-won Hints

> *"Experience is the comb life hands you after you've already lost your hair."*

- **Stop guessing at CRF.** `--target-vmaf 93` samples the file and finds the CRF that hits
  your quality target. A number you can reason about beats a dial with no units.
- **NVENC for bulk, x265 for masters.** Fixed-function silicon is ~15× faster at the same
  look for a slightly bigger file. Previews and the whole-library grind → NVENC. The one
  you'll keep forever → x265 `slow`.
- **10-bit from an 8-bit source isn't snake oil.** It gives the encoder headroom and kills
  banding in gradients — clean skies, clean fades — for a little size. The restore profile
  does it for you.
- **Chunked encoding is no free lunch.** It only recovers cores your encoder wasn't already
  using. x265 `slow` already saturates a big box; SVT-AV1 threads itself. It earns its keep
  with poorly-threaded encoders, huge core counts, and resumability.
- **The output extension picks the container.** `-o out.mp4` gives you MP4, `-o out.mkv`
  gives you Matroska — no `--container` needed. moist checks the codec fits the box *before*
  it burns an encode, so a doomed pair fails in a second, not an hour.
- **Let a folder do the work.** `moist watch ./inbox --output-dir ./done --profile "Restore
  SD to HD (vintage)"` encodes anything dropped in, waits for the copy to finish, and survives a reboot.
- **Look the tapes over, before and after.** `moist probe file.avi` shows resolution, fields,
  and bit depth. `moist health ./library --deep` decodes every file and flags the ones rotting
  from the inside.
- **`--dry-run` shows the plan.** Every job and the exact FFmpeg command, without encoding a
  single frame. Look before you leap.

---

## FAQ

**Does moist upscale with AI, like Topaz?**
Yes, two ways, and it's upfront about the limits of both. The **GPU** path runs a small neural
shader (FSRCNNX, or Anime4K for animation) through FFmpeg's libplacebo in the same pass as the
encode: fast, and it degrades to a plain spline scale on machines without a Vulkan GPU. The
**Real-ESRGAN** path runs a deep model frame by frame: the sharpest texture, roughly 30× slower,
for the clips you care most about. Both clean and enlarge what's there. Neither can recover
detail the source never recorded, and moist won't pretend otherwise. If you'd rather keep a
source exactly as it looks, the *Faithful* broadcast profiles skip upscaling and denoising
entirely.

**Which video encoder should I use?**
x265 (`ffmpeg-x265`) for quality masters. NVENC (`hw-nvenc-hevc`) when you've got an NVIDIA
GPU and a stack of files. SVT-AV1 (`ffmpeg-svtav1`) for the smallest files if you don't mind
the wait. x264 (`ffmpeg-x264`) when a device won't play anything newer.

**What CRF should I use?**
Don't. Use `--target-vmaf 93` (good) or `95` (near-transparent) and let moist find it. If you
insist: x265 CRF 18–20 is visually excellent for most content.

**My `.mp4` came out as Matroska, or my codec got rejected.**
Older builds ignored the output extension. Current moist reads it: `-o out.mp4` is MP4,
`-o out.avi` is AVI, and it warns up front if the codec can't live in that container.

**NVENC (or Quick Sync / AMF) doesn't show up.**
moist test-encodes each hardware path and only offers the ones that actually work. Run
`moist list hardware` — it prints why each missing one failed. A line in `ffmpeg -encoders`
means the binary was *compiled* with it, not that your machine can run it. Update your GPU
driver and try again.

**My restored file looks great, but faces in motion are still soft.**
That's motion blur baked into the source frame — no filter brings back detail the tape never
recorded. Static shots (graphics, standing figures, crowds) sharpen up dramatically; fast
motion stays as sharp as the original exposure allowed.

**4:3 or 16:9 — will the restore profile stretch my video?**
No. The upscale targets 1080p *height* and keeps the source shape. A 4:3 tape becomes
1440×1080; a 16:9 one becomes 1920×1080.

**Can I run it headless — on a server or in Docker?**
Yes. `moist serve` exposes the queue over HTTP (authenticated, loopback by default), and the
Docker image runs it out of the box. The CLI does everything the GUI does.

---

## Troubleshooting

**FFmpeg not found** — Set the path in Settings, or put `ffmpeg` and `ffprobe` on your PATH. Or click "Install" in the Settings prerequisites panel.

**Build errors after pulling** — `cd src && dotnet restore && dotnet build`

**Encoding fails with "No processor found"** — The selected encoder's tool isn't installed or the path isn't configured in Settings.

**Audio-only output has wrong extension** — The extension auto-updates when you toggle Audio Only mode. If you set the output path manually, change the extension to match the codec (.opus, .m4a, .mp3, .flac).

**Theme not applying** — Click "Save Settings" after changing the theme. It applies on save.

**Two-pass encoding produces no output** — Check that the output directory is writable and has enough disk space. Two-pass creates temporary log files next to the output.

**Hardware encoder missing from the list** — moist only offers GPU encoders that pass a real test-encode. `moist list hardware` shows why each absent one failed (no driver, no silicon, disabled in the build). NVENC needs a reasonably current NVIDIA driver.

**Full-length x265 encode takes hours** — 1080p at `slow` is genuinely heavy work. Drop to `--preset medium`, or hand it to the GPU with `--video-encoder hw-nvenc-hevc`, for anything short of a final master.

**Output has visible banding** — encode 10-bit (`--bit-depth 10`) and add `--filter deband`. Banding is an 8-bit gradient problem; 10-bit gives the encoder room. The "Restore SD to HD" profile already does both.

**Watch folder keeps re-encoding the same file** — keep the output directory *outside* the watched folder. moist refuses an output dir nested inside the watch path for exactly this reason, and a move-destination inside it will loop.

**Restore profile came out the wrong size** — it targets 1080p height and keeps aspect. If a source is anamorphic (stored 720×480 but meant for 16:9), set the size yourself: `--filter resize:width=1920,height=1080`.
