# moist — command line

[← back to the README](../README.md) · [Features](features.md) · [Command line](cli.md) · [FAQ](faq.md) · [Development](development.md)

## Everyday commands


```bash
moist encode input.mkv -o output.mp4 --crf 20 --preset slow
moist encode input.mkv --profile "Web - H.265 High Quality"

# one-click: restore an old SD tape to sharp 10-bit 1080p (filters + upscale from the profile)
moist encode "old_tape.avi" -o "old_tape - restored.mkv" --profile "Restore SD to HD (vintage)"

# over-the-air captures of film-sourced TV (MeTV-style 720p59.94, bob-upconverted film, pillarboxed):
# crop the bars, inverse-telecine back to 23.976, clean, upscale to 1080p. B&W seasons lose the chroma noise.
moist batch "Season 10/*.ts" --output-dir restored --suffix "" --profile "Broadcast 720p60 Film - B&W to 1080p"
moist batch "Season 12/*.ts" --output-dir restored --suffix "" --profile "Broadcast 720p60 Film - Colour to 1080p"
# ...or keep the broadcast exactly as it moves and looks, just tightened (native 720p60, grain kept, AC3 untouched)
moist batch "Season 12/*.ts" --output-dir faithful --suffix "" --profile "Broadcast 720p60 Film - Faithful 720p (Colour)"
moist encode "episode.ts" -o "episode - premium.mkv" --profile "Broadcast 720p60 Film - Colour to 1080p (Premium Real-ESRGAN)"
moist encode input.mkv --audio-only --audio-encoder ffmpeg-audio-flac
moist encode input.mkv --mode twopass --bitrate 8000 --container mp4
moist encode input.mkv --filter resize:width=1920,height=1080 --filter unsharp:luma_amount=1.2

# say what quality you want instead of guessing a CRF
moist encode input.mkv --target-vmaf 95 --measure
moist crf-search input.mkv --target-vmaf 95 --json

# hardware, all audio tracks, forced 10-bit
moist encode input.mkv --video-encoder hw-nvenc-hevc --audio-track all --bit-depth 10

# split into parallel chunks; resumes if interrupted
moist encode input.mkv --chunked --jobs 8

# every scene gets its own CRF, aimed at the same quality throughout
moist encode input.mkv --chunked --per-scene --target-vmaf 95

moist batch *.mkv --container mp4 --output-dir ./out --jobs 4 --keep-going
moist compare source.mkv encoded.mkv --metric vmaf
moist probe input.mkv --json
moist list encoders | hardware | containers | profiles | filters
moist tools
```

## Automation

```bash
# encode whatever lands in a folder, with any encode option you like
moist watch ./inbox --output-dir ./encoded --crf 22 --preset slow
moist watch /mnt/dropbox --output-dir /mnt/library -r --on-success move --move-to ./done

# find damaged files; exit code 1 if any, so it can drive a scheduled check
moist health /media/films
moist health /media/films --deep --json > report.json

# HTTP API over the same queue
moist serve --port 7788                                   # loopback, token required
moist serve --host 0.0.0.0 --root /srv/media --token "$T"   # on the network: confined to a root
```

A file that has just appeared in a watch folder is usually still being copied — it
has a valid header and a plausible duration the whole way through, so encoding it
early produces a truncated result with nothing to explain it. `moist watch` waits
for a file to stop changing first, remembers what it has already encoded across
restarts, and only deletes or moves a source once its encode has succeeded.

`moist health` without `--deep` reads container headers, which catches an
interrupted write but not damage inside an otherwise complete file — that has a
perfectly good header too. `--deep` decodes everything and compares the frames it
actually got against what the container claims.

`moist serve` authenticates by default (a bearer token, from `--token` or `MOIST_TOKEN`),
binds to loopback by default, and refuses to do neither. Off loopback it needs `--root <dir>`:
every request's input and output must resolve under a root. Submitted options are parsed by the
same code as the command line, so `{"crf": 22}` means exactly what `--crf 22` means — but only
options that shape the encode are accepted; tool paths and raw encoder arguments are refused.
See [SECURITY.md](../SECURITY.md) for the full model.

## Docker

```bash
docker build -t moist .
docker run -p 7788:7788 -v /media:/media -e MOIST_TOKEN=secret moist
docker run --rm -v /media:/media moist health /media --deep
```

The image is built on Ubuntu 24.04, whose FFmpeg has x264, x265, VP9 and SVT-AV1 but
**not** libvmaf, so `--target-vmaf` is unavailable in the stock image (the XPSNR, SSIM
and PSNR targets still work). `moist tools` reports what was actually found.

- `--dry-run` prints the plan (including the exact FFmpeg commands) without encoding
- `--json` emits machine-readable results; logs go to stderr so stdout stays clean for pipes
- Exit codes: `0` success, `1` encode failed, `2` bad usage, `3` missing prerequisite
- `Ctrl+C` cancels gracefully and kills the FFmpeg process tree
