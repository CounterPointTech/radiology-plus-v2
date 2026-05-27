using System.Globalization;

namespace RadiologyPlus.Scripting;

internal static class ParameterSubstitution
{
    /// <summary>
    /// Replaces `{{Name}}` tokens with literal SQL-quoted values. Strings get single-quoted
    /// (with `'` doubled); numbers and bools are inlined as-is.
    /// </summary>
    public static string SubstituteSqlStyle(string body, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (parameters is null || parameters.Count == 0) return body;

        var result = body;
        foreach (var (k, v) in parameters)
        {
            var token = "{{" + k + "}}";
            var literal = ToSqlLiteral(v);
            result = result.Replace(token, literal, StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    private static string ToSqlLiteral(object? v) => v switch
    {
        null => "NULL",
        bool b => b ? "TRUE" : "FALSE",
        int or long or short or byte or sbyte or uint or ulong or ushort => Convert.ToString(v, CultureInfo.InvariantCulture)!,
        float or double or decimal => Convert.ToString(v, CultureInfo.InvariantCulture)!,
        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
        DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss zzz}'",
        Guid g => $"'{g}'",
        _ => $"'{v.ToString()?.Replace("'", "''") ?? string.Empty}'",
    };
}

internal static class SqlStatementSplitter
{
    public static List<string> SplitPostgres(string script)
    {
        var statements = new List<string>();
        var current = new System.Text.StringBuilder();
        var inString = false;
        var inLineComment = false;
        var prev = '\0';

        foreach (var ch in script)
        {
            if (!inLineComment && ch == '\'' && prev != '\\') inString = !inString;
            if (!inString && prev == '-' && ch == '-') inLineComment = true;
            if (inLineComment && ch == '\n') inLineComment = false;

            if (ch == ';' && !inString && !inLineComment)
            {
                var s = current.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(s)) statements.Add(s);
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
            prev = ch;
        }

        var tail = current.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(tail)) statements.Add(tail);
        return statements;
    }
}
