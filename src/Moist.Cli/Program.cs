namespace Moist.Cli;

public static class Program
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;
    public const int ExitMissingTool = 3;

    public static async Task<int> Main(string[] argv)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (argv.Length == 0)
        {
            Help.PrintUsage();
            return ExitUsage;
        }

        var command = argv[0].ToLowerInvariant();
        var rest = argv.Skip(1).ToArray();

        if (command is "-h" or "--help" or "help")
        {
            Help.PrintUsage(rest.FirstOrDefault());
            return ExitOk;
        }

        if (command is "--version" or "version")
        {
            Console.WriteLine($"moist {typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown"}");
            return ExitOk;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // First Ctrl+C cancels gracefully so running FFmpeg processes get killed
            // and temp files are dealt with; a second one is left to the runtime.
            if (!cancellation.IsCancellationRequested)
            {
                e.Cancel = true;
                Console.Error.WriteLine("\nCancelling… (press Ctrl+C again to force quit)");
                cancellation.Cancel();
            }
        };

        try
        {
            var cl = CommandLine.Parse(rest);

            if (cl.Flag("help", "h"))
            {
                Help.PrintUsage(command);
                return ExitOk;
            }

            var exit = command switch
            {
                "encode" => await EncodeCommand.RunAsync(cl, cancellation.Token),
                "batch" => await BatchCommand.RunAsync(cl, cancellation.Token),
                "probe" => await ProbeCommand.RunAsync(cl, cancellation.Token),
                "compare" => await CompareCommand.RunAsync(cl, cancellation.Token),
                "crf-search" or "search" => await CrfSearchCommand.RunAsync(cl, cancellation.Token),
                "watch" => await WatchCommand.RunAsync(cl, cancellation.Token),
                "health" => await HealthCommand.RunAsync(cl, cancellation.Token),
                "list" => ListCommand.Run(cl),
                "tools" => await ToolsCommand.RunAsync(cl, cancellation.Token),
                _ => UnknownCommand(command),
            };

            return exit;
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            Console.Error.WriteLine($"Try 'moist {command} --help'.");
            return ExitUsage;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return ExitFailed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ExitFailed;
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"error: unknown command '{command}'");
        Help.PrintUsage();
        return ExitUsage;
    }
}
