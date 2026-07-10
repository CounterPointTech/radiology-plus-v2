using Cronos;

namespace RadiologyPlus.Scripting;

/// <summary>
/// Shared cron parsing that matches ScriptScheduler's behavior exactly:
/// try 6-field (with seconds) first, fall back to the standard 5-field form,
/// and evaluate occurrences in UTC (the scheduler ticks in UTC).
/// </summary>
public static class CronHelper
{
    public static bool TryParse(string? expression, out CronExpression? cron)
    {
        cron = null;
        if (string.IsNullOrWhiteSpace(expression)) return false;
        try
        {
            cron = CronExpression.Parse(expression, CronFormat.IncludeSeconds);
            return true;
        }
        catch (CronFormatException)
        {
            try
            {
                cron = CronExpression.Parse(expression);
                return true;
            }
            catch (CronFormatException)
            {
                return false;
            }
        }
    }

    /// <summary>Next UTC occurrence after <paramref name="fromUtc"/>, or null when the expression is blank/invalid.</summary>
    public static DateTimeOffset? NextOccurrenceUtc(string? expression, DateTimeOffset fromUtc)
    {
        if (!TryParse(expression, out var cron) || cron is null) return null;
        var next = cron.GetNextOccurrence(fromUtc.UtcDateTime, TimeZoneInfo.Utc);
        return next is null ? null : new DateTimeOffset(next.Value, TimeSpan.Zero);
    }
}
