using System.Globalization;

namespace Moist.Cli;

/// <summary>
/// Minimal option parser. Deliberately dependency-free — the CLI ships as a
/// self-contained binary and shouldn't pull in a preview NuGet package for this.
///
/// Accepts <c>--name value</c>, <c>--name=value</c>, <c>-n value</c> and bare flags.
/// Everything not attached to an option is treated as a positional argument.
/// </summary>
public sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = [];
    private readonly HashSet<string> _consumed = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Positional => _positional;

    /// <summary>Options that take no value, so "--audio-only input.mp4" doesn't eat the filename.</summary>
    private static readonly HashSet<string> KnownFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio-only", "no-subtitles", "no-chapters", "no-attachments", "no-metadata",
        "dry-run", "json", "overwrite", "y", "measure", "no-cache",
        "quiet", "q", "verbose", "v", "help", "h", "version", "no-progress", "keep-going",
    };

    public static CommandLine Parse(IEnumerable<string> args)
    {
        var cl = new CommandLine();
        var queue = new Queue<string>(args);

        while (queue.Count > 0)
        {
            var arg = queue.Dequeue();

            if (arg == "--")
            {
                while (queue.Count > 0) cl._positional.Add(queue.Dequeue());
                break;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var body = arg[2..];
                AddOption(cl, body, queue);
            }
            else if (arg.Length > 1 && arg[0] == '-' && !File.Exists(arg))
            {
                var body = arg[1..];
                AddOption(cl, body, queue);
            }
            else
            {
                cl._positional.Add(arg);
            }
        }

        return cl;
    }

    private static void AddOption(CommandLine cl, string body, Queue<string> queue)
    {
        string name, value;
        var eq = body.IndexOf('=');
        if (eq >= 0)
        {
            name = body[..eq];
            value = body[(eq + 1)..];
        }
        else
        {
            name = body;
            if (KnownFlags.Contains(name) || queue.Count == 0 || queue.Peek().StartsWith("--", StringComparison.Ordinal))
                value = "true";
            else
                value = queue.Dequeue();
        }

        if (!cl._options.TryGetValue(name, out var list))
            cl._options[name] = list = [];
        list.Add(value);
    }

    private string? Raw(params string[] names)
    {
        foreach (var n in names)
        {
            if (_options.TryGetValue(n, out var v) && v.Count > 0)
            {
                _consumed.Add(n);
                return v[^1];
            }
        }
        return null;
    }

    public bool Has(params string[] names) => names.Any(n =>
    {
        if (!_options.ContainsKey(n)) return false;
        _consumed.Add(n);
        return true;
    });

    public string? String(string name, string? shortName = null)
        => shortName is null ? Raw(name) : Raw(name, shortName);

    public IReadOnlyList<string> Many(string name)
    {
        if (!_options.TryGetValue(name, out var v)) return [];
        _consumed.Add(name);
        return v;
    }

    public bool Flag(string name, string? shortName = null)
    {
        var raw = shortName is null ? Raw(name) : Raw(name, shortName);
        return raw is not null && raw is not "false" and not "0";
    }

    public int? Int(string name, string? shortName = null)
    {
        var raw = shortName is null ? Raw(name) : Raw(name, shortName);
        if (raw is null) return null;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        throw new UsageException($"--{name} expects a whole number, got '{raw}'");
    }

    public double? Double(string name, string? shortName = null)
    {
        var raw = shortName is null ? Raw(name) : Raw(name, shortName);
        if (raw is null) return null;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        throw new UsageException($"--{name} expects a number, got '{raw}'");
    }

    public TEnum? Enum<TEnum>(string name) where TEnum : struct, Enum
    {
        var raw = Raw(name);
        if (raw is null) return null;
        if (System.Enum.TryParse<TEnum>(raw.Replace("-", ""), ignoreCase: true, out var e)) return e;

        var valid = string.Join(", ", System.Enum.GetNames<TEnum>().Select(n => n.ToLowerInvariant()));
        throw new UsageException($"--{name} expects one of: {valid} (got '{raw}')");
    }

    /// <summary>Options the command never looked at — almost always a typo worth reporting.</summary>
    public IEnumerable<string> UnknownOptions()
        => _options.Keys.Where(k => !_consumed.Contains(k));
}

/// <summary>Thrown for bad input from the user; produces exit code 2 and no stack trace.</summary>
public sealed class UsageException(string message) : Exception(message);
