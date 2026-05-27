namespace RadiologyPlus.Migrator;

/// <summary>
/// Minimal arg parser for our CLI. Supports a single leading subcommand plus <c>--key=value</c>
/// or <c>--key value</c> flags. Boolean flags without a value default to <c>true</c>.
/// </summary>
internal static class ArgParser
{
    public static (IReadOnlyList<string> Positional, IReadOnlyDictionary<string, string> Flags) Parse(string[] args)
    {
        var positional = new List<string>();
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var rest = arg[2..];
                var eq = rest.IndexOf('=');
                if (eq >= 0)
                {
                    flags[rest[..eq]] = rest[(eq + 1)..];
                }
                else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    flags[rest] = args[i + 1];
                    i++;
                }
                else
                {
                    flags[rest] = "true";
                }
            }
            else
            {
                positional.Add(arg);
            }
        }

        return (positional, flags);
    }
}

internal sealed class UsageException(string message) : Exception(message);

internal static class FlagExtensions
{
    public static string Required(this IReadOnlyDictionary<string, string> flags, string name) =>
        flags.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new UsageException($"Missing required flag --{name}");

    public static string Optional(this IReadOnlyDictionary<string, string> flags, string name, string fallback) =>
        flags.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    public static int OptionalInt(this IReadOnlyDictionary<string, string> flags, string name, int fallback) =>
        flags.TryGetValue(name, out var v) && int.TryParse(v, out var parsed) ? parsed : fallback;

    public static int RequiredInt(this IReadOnlyDictionary<string, string> flags, string name) =>
        flags.TryGetValue(name, out var v) && int.TryParse(v, out var parsed)
            ? parsed
            : throw new UsageException($"--{name} is required and must be an integer.");

    public static bool OptionalBool(this IReadOnlyDictionary<string, string> flags, string name, bool fallback) =>
        flags.TryGetValue(name, out var v) && bool.TryParse(v, out var parsed) ? parsed : fallback;
}
