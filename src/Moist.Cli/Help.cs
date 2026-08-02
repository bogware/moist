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
            case "list": List(); return;
            case "tools": Tools(); return;
        }

        Console.WriteLine("""
            moist — cross-platform video encoder

            USAGE
              moist <command> [options]

            COMMANDS
              encode <input>        Encode a single file
              batch <inputs...>     Encode many files (accepts globs)
              probe <input>         Show media information
              list <what>           List encoders, containers, profiles or filters
              tools                 Check external tool prerequisites

            GLOBAL
              -h, --help            Show help (use 'moist <command> --help' for details)
              --version             Print version

            EXAMPLES
              moist encode in.mkv -o out.mp4 --crf 20 --preset slow
              moist encode in.mkv --profile "Web - H.265 High Quality"
              moist encode in.mkv --audio-only --audio-encoder ffmpeg-audio-flac
              moist batch *.mkv --container mp4 --jobs 4
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
              --no-chapters         Don't carry chapter markers over
              --no-attachments      Don't carry attached fonts over (MKV only)
              --no-metadata         Don't carry container-level tags over

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

    private static void Tools() => Console.WriteLine("""
        moist tools — check external prerequisites

        USAGE
          moist tools [--json]

        Reports which tools were found, their versions and where they came from.
        Exit code 3 if a required tool (FFmpeg, FFprobe) is missing.
        """);
}
