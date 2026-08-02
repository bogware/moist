namespace Moist.Cli;

public static class Help
{
    public static void PrintUsage(string? command = null)
    {
        switch (command?.ToLowerInvariant())
        {
            case "encode": Encode(); return;
            case "batch": Batch(); return;
            case "probe": Probe(); return;
            case "compare": Compare(); return;
            case "crf-search" or "search": CrfSearch(); return;
            case "list": List(); return;
            case "tools": Tools(); return;
            case "watch": Watch(); return;
            case "health": Health(); return;
        }

        Console.WriteLine("""
            moist — cross-platform video encoder

            USAGE
              moist <command> [options]

            COMMANDS
              encode <input>        Encode a single file
              batch <inputs...>     Encode many files (accepts globs)
              probe <input>         Show media information
              compare <a> <b>       Measure quality of an encode against its source
              crf-search <input>    Find the CRF that reaches a quality target
              watch <folder>        Encode whatever lands in a folder
              health <folder>       Find damaged or unplayable files in a library
              list <what>           List encoders, hardware, containers, profiles or filters
              tools                 Check external tool prerequisites

            GLOBAL
              -h, --help            Show help (use 'moist <command> --help' for details)
              --version             Print version

            EXAMPLES
              moist encode in.mkv -o out.mp4 --crf 20 --preset slow
              moist encode in.mkv --target-vmaf 95            # pick quality, not CRF
              moist encode in.mkv --profile "Web - H.265 High Quality"
              moist encode in.mkv --audio-only --audio-encoder ffmpeg-audio-flac
              moist encode in.mkv --chunked --jobs 8          # parallel, resumable
              moist batch *.mkv --container mp4 --jobs 4
              moist watch ./inbox --output-dir ./done --crf 22
              moist health /media/films --deep
              moist compare source.mkv encoded.mkv
              moist probe in.mkv --json
            """);
    }

    private static void Encode() => Console.WriteLine("""
        moist encode — encode a single file

        USAGE
          moist encode <input> [-o <output>] [options]

        OUTPUT
          -o, --output <path>       Output file (default: <input>_encoded.<ext>)
              --container <fmt>     mkv | mp4 | webm | avi | ts | mov   (default: mkv)
          -y, --overwrite           Overwrite the output instead of auto-renaming

        VIDEO
              --video-encoder <id>  Encoder id (see 'moist list encoders'). Default ffmpeg-x265
              --mode <mode>         crf | quality | bitrate | twopass    (default: crf)
              --crf <n>             Quality for crf/quality modes       (default: 23)
              --bitrate <kbps>      Target bitrate for bitrate/twopass modes
              --preset <name>       ultrafast … placebo                 (default: medium)
              --tune <name>         film, animation, grain, …
              --av1-preset <0-13>   SVT-AV1 speed preset
              --bit-depth <d>       auto | 8 | 10 | 12                  (default: auto = match source)
              --chroma <c>          auto | 420 | 422 | 444              (default: auto = match source)
              --av1-tune <name>     vq | psnr | ssim | iq | ms-ssim | vmaf
                                    SVT-AV1 only. 'vq' is the perceptual tuning that
                                    SVT-AV1 4.0 merged from the PSY fork; the encoder's
                                    own default optimises PSNR instead.
              --video-args <args>   Extra arguments passed to the encoder verbatim

        AUDIO
              --audio-encoder <id>  Default ffmpeg-audio-opus. Use ffmpeg-audio-copy to passthrough
              --audio-bitrate <k>   Default 128
              --audio-mode <mode>   vbr | cbr | constrainedvbr
              --channels <mode>     keeporiginal | mono | stereo | surround51 | surround71
              --sample-rate <hz>    0 keeps the source rate
              --audio-track <n>     Source audio stream index. Repeatable, comma-separated,
                                    or 'all' to keep every track       (default: 0)
              --normalize <mode>    none | ebur128 | peak | rms
              --normalize-target <lufs>   Default -16
              --true-peak <db>      Default -1
              --audio-args <args>   Extra arguments passed to the encoder verbatim
              --audio-only          Encode audio only; no video, no mux

        FILTERS / PROFILES
              --profile <name>      Apply a saved profile first; explicit flags still win
              --filter <spec>       Repeatable. e.g. --filter resize:width=1280,height=720
              --no-subtitles        Don't copy subtitles from the source
              --subtitle <file>     Mux an external subtitle file
              --subtitle-track <n>  Keep only these source subtitle streams. Repeatable
                                    or comma-separated; default keeps all of them.
              --burn-subtitles      Render subtitles into the picture permanently, for
                                    players that can't display them. Uses --subtitle when
                                    given, otherwise --burn-subtitle-track from the source.
                                    Cannot be combined with --chunked.
              --burn-subtitle-track <n>  Which source subtitle stream to burn in
              --no-chapters         Don't carry chapter markers over
              --no-attachments      Don't carry attached fonts over (MKV only)
              --no-metadata         Don't carry container-level tags over

        TARGET QUALITY
              --target-vmaf <n>     Find and use the CRF that reaches this VMAF score.
                                    Also --target-xpsnr / --target-ssim / --target-psnr,
                                    or --target-quality <n> with --metric.
              --metric <name>       vmaf | xpsnr | ssim | psnr          (default: vmaf)
              --samples <n>         Spans of the source to test         (default: 3)
              --sample-duration <s> Seconds per sample                  (default: 5)
              --max-probes <n>      Cap on CRFs tried                   (default: 8)
              --no-cache            Ignore a previous search for this file
              --measure             Score the finished encode against the source

        CHUNKED ENCODING
              --chunked             Split the video into chunks, encode them in parallel
                                    and join them back together
              --chunk-mode <mode>   scene | fixed                       (default: scene)
              --max-chunk <s>       Longest chunk in seconds            (default: 60)
              --min-chunk <s>       Shortest chunk in seconds           (default: 5)
              --scene-threshold <n> Scene detection sensitivity, lower finds more cuts
              --no-resume           Re-encode chunks left by an interrupted run
              --no-verify           Skip the frame-count check on the joined result

        PER-SCENE QUALITY  (needs --chunked and a target)
              --per-scene           Give every scene its own CRF so the target is reached
                                    everywhere, instead of over-spending on easy scenes
                                    and starving hard ones
              --per-scene-full      Search every scene properly instead of modelling most
                                    of them. Slower, marginally more accurate.
              --calibration-scenes <n>  Scenes to search before modelling the rest (default 3)

          A single CRF gives every scene the same effort, not the same quality. On a clip
          whose scenes differ sharply, fixed CRF 25 averaged VMAF 93.4 but its hardest
          scene sat at 83.6, while per-scene held every scene at 93.8+. Against a fixed
          CRF chosen low enough that no scene falls short, per-scene came out 15% smaller.

          Scenes the model predicts are then checked and re-searched if they miss, because
          compressibility and perceptual tolerance are not the same thing — a test pattern
          compresses to nothing yet its sharp edges fall apart when quantised hard.

          How much this helps depends entirely on whether your encoder can already use
          all your cores. A well-threaded x265 on a 16-core machine leaves little room,
          so expect around 1.2x. It pays off with encoders that thread poorly, with very
          high core counts, and — regardless of speed — because an interrupted encode
          picks up where it left off instead of starting over.

        BEHAVIOUR
          -j, --jobs <n>            Concurrent jobs                     (default: from settings)
              --ffmpeg <path>       Override the FFmpeg binary
              --ffprobe <path>      Override the FFprobe binary
              --temp-dir <path>     Override the temp directory
              --dry-run             Print the planned commands and exit
              --json                Emit a machine-readable result
          -q, --quiet               Errors only
              --no-progress         Disable the progress line
        """);

    private static void Batch() => Console.WriteLine("""
        moist batch — encode many files

        USAGE
          moist batch <inputs...> [options]

        Takes every option 'moist encode' does, applied to each input, plus:
              --output-dir <path>   Where to write outputs (default: alongside each input)
              --suffix <text>       Appended to each output name        (default: _encoded)
              --keep-going          Continue after a file fails

        Inputs may be paths or globs (*.mkv). Globs are expanded by moist when the
        shell hasn't already done it.

        EXAMPLES
          moist batch *.mkv --container mp4 --crf 22 --output-dir ./out
          moist batch season1/*.mkv --audio-only --audio-encoder ffmpeg-audio-opus
        """);

    private static void Probe() => Console.WriteLine("""
        moist probe — show media information

        USAGE
          moist probe <input> [--json]

        Prints duration, video and audio streams, subtitle tracks and frame count
        as reported by ffprobe.
        """);

    private static void List() => Console.WriteLine("""
        moist list — enumerate what's available

        USAGE
          moist list <what> [--json]

        WHAT
          encoders          Video and audio encoders (hardware ones only if usable)
          video-encoders    Video only
          audio-encoders    Audio only
          hardware          GPU encoders, with the reason any are unavailable
          containers        Containers and the codecs each can carry
          profiles          Saved encoding profiles
          filters           Filters available to --filter

        Hardware encoders are probed by encoding a few frames, because an encoder being
        compiled into FFmpeg says nothing about the GPU actually being present.
        """);

    private static void Compare() => Console.WriteLine("""
        moist compare — measure an encode against its source

        USAGE
          moist compare <reference> <encoded> [--metric <name>] [--json]

        OPTIONS
          --metric <name>   vmaf | xpsnr | ssim | psnr    (default: vmaf)
          --threads <n>     Threads for the metric        (default: FFmpeg decides)
          --json            Machine-readable output

        VMAF also reports the harmonic mean, which penalises bad scenes far more than
        the plain average — a file can average 95 and still have a stretch that looks
        poor. Check the worst-frame figure too.

        A downscaled encode is scaled back up to the reference resolution first,
        otherwise the two can't be compared at all.
        """);

    private static void CrfSearch() => Console.WriteLine("""
        moist crf-search — find the CRF that reaches a quality target

        USAGE
          moist crf-search <input> --target-vmaf 95 [options]

        Encoding the whole file at every candidate CRF would take longer than just
        encoding it, so this samples a few short spans instead and searches over those.
        On a test clip, three five-second samples predicted the full encode's VMAF to
        within 0.1.

        The search is an interpolated bisection: it fits a line through the two nearest
        measurements and jumps to where that line crosses the target, which usually
        lands within a point or two immediately.

        OPTIONS
          --target-vmaf <n>     Or --target-xpsnr / --target-ssim / --target-psnr
          --metric <name>       Used with --target-quality
          --samples <n>         Spans to test               (default: 3)
          --sample-duration <s> Seconds per sample          (default: 5)
          --max-probes <n>      Cap on CRFs tried           (default: 8)
          --no-cache            Ignore a previous search for this file

        Accepts the encoder options 'moist encode' does — the search uses the encoder
        and preset you will actually encode with, since the CRF that hits a score on
        one encoder does not hit it on another.

        Results are cached per file, encoder, preset and target. Editing the source
        invalidates the entry automatically.

        Exit code 1 if the target could not be reached; the best CRF found is still
        reported.
        """);

    private static void Tools() => Console.WriteLine("""
        moist tools — check external prerequisites

        USAGE
          moist tools [--json]

        Reports which tools were found, their versions and where they came from.
        Exit code 3 if a required tool (FFmpeg, FFprobe) is missing.
        """);

    private static void Watch() => Console.WriteLine("""
        moist watch — encode whatever lands in a folder

        USAGE
          moist watch <folder> --output-dir <dir> [encode options]

        WATCHING
          --output-dir <dir>    Where encodes are written (required, must be outside
                                the watched folder)
          -r, --recursive       Include subdirectories
          --extensions <list>   Comma-separated, e.g. mkv,mp4  (default: common video)
          --interval <sec>      How often to re-list the folder      (default: 10)
          --stable-for <sec>    How long a file must stop changing before it counts
                                as finished being written            (default: 30)
          --new-only            Ignore files already there when the watch starts

        AFTER A SUCCESSFUL ENCODE
          --on-success <what>   keep | move | delete                 (default: keep)
          --move-to <dir>       Destination for --on-success move

        Every 'moist encode' option works here and applies to each file, so the watch
        encodes exactly the way the equivalent encode command would.

        Files are only picked up once their size and timestamp have held still for
        --stable-for. A file still being copied in has a valid header and a plausible
        duration, so encoding one early produces a truncated result and no error.

        What has been encoded is remembered between runs, so restarting the watcher
        does not re-encode the folder. A file that changes afterwards is treated as
        new work.

        EXAMPLES
          moist watch ./inbox --output-dir ./encoded --crf 22 --preset slow
          moist watch /mnt/dropbox --output-dir /mnt/library -r --on-success delete
          moist watch ./inbox --output-dir ./done --target-vmaf 95 --json
        """);

    private static void Health() => Console.WriteLine("""
        moist health — find damaged or unplayable files

        USAGE
          moist health <folder> [--deep] [--json]

        OPTIONS
          --deep                Decode every file in full rather than only reading
                                its header
          --no-recursive        Scan only the top directory
          --extensions <list>   Comma-separated, e.g. mkv,mp4
          --json                Machine-readable report

        Without --deep this reads container headers, which catches files whose write
        was interrupted but not damage inside an otherwise complete file — that has a
        perfectly good header and a plausible duration.

        --deep decodes everything and discards it, reporting what FFmpeg complains
        about, and compares the frames actually decoded against the number the
        container claims. It takes roughly as long as playing the library.

        Exit code 1 if any file has a problem, so this can drive a scheduled check.

        EXAMPLES
          moist health /media/films
          moist health /media/films --deep --json > report.json
        """);
}
