namespace DynamicEndpoints.Cli;

/// <summary>Usage errors – reported with exit code <see cref="ExitCodes.Usage"/>.</summary>
internal sealed class CliUsageException(string message) : Exception(message);

/// <summary>
/// Minimal argument parser: <c>command [positional…] [--option value | --option=value | --flag | -o value]</c>.
/// Options may appear anywhere; <c>--</c> ends option parsing.
/// </summary>
internal sealed class CliArguments
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["-o"] = "--output",
        ["-H"] = "--header",
        ["-f"] = "--format",
        ["-m"] = "--mode",
        ["-u"] = "--url",
        ["-h"] = "--help",
        ["-v"] = "--verbose",
    };

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal)
    {
        "--help", "--version", "--sync", "--dry-run", "--json", "--enabled", "--skip-invalid", "--verbose",
    };

    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public string? Command { get; private set; }

    public List<string> Positional { get; } = [];

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        var result = new CliArguments();
        var optionsEnded = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!optionsEnded && arg == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (!optionsEnded && arg.Length > 1 && arg[0] == '-')
            {
                string name;
                string? value = null;
                var equals = arg.IndexOf('=');
                if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 2)
                {
                    name = arg[..equals];
                    value = arg[(equals + 1)..];
                }
                else
                {
                    name = Aliases.GetValueOrDefault(arg, arg);
                }

                if (Flags.Contains(name))
                {
                    if (value is not null)
                    {
                        throw new CliUsageException($"'{name}' doesn't take a value.");
                    }

                    value = "true";
                }
                else if (value is null)
                {
                    if (i + 1 >= args.Count)
                    {
                        throw new CliUsageException($"'{name}' needs a value.");
                    }

                    value = args[++i];
                }

                if (!result._options.TryGetValue(name, out var values))
                {
                    result._options[name] = values = [];
                }

                values.Add(value);
                continue;
            }

            if (result.Command is null)
            {
                result.Command = arg;
            }
            else
            {
                result.Positional.Add(arg);
            }
        }

        return result;
    }

    public bool Flag(string name)
    {
        _used.Add(name);
        return _options.ContainsKey(name);
    }

    public string? Value(string name)
    {
        _used.Add(name);
        return _options.TryGetValue(name, out var values) ? values[^1] : null;
    }

    public IReadOnlyList<string> Values(string name)
    {
        _used.Add(name);
        return _options.TryGetValue(name, out var values) ? values : [];
    }

    /// <summary>Fails for options the command didn't ask for – catches typos such as <c>--dryrun</c>.</summary>
    public void EnsureAllUsed()
    {
        if (_options.Keys.FirstOrDefault(k => !_used.Contains(k)) is { } unknown)
        {
            throw new CliUsageException($"Unknown option '{unknown}' for '{Command}'. Run 'dynamic-endpoints --help'.");
        }
    }
}

internal static class ExitCodes
{
    public const int Success = 0;

    /// <summary>The server rejected the request, e.g. invalid definitions – nothing was written.</summary>
    public const int Failed = 1;

    /// <summary><c>diff</c> found differences.</summary>
    public const int Differences = 2;

    public const int Usage = 3;

    /// <summary>The server could not be reached or answered with an unexpected status.</summary>
    public const int Connection = 4;
}
