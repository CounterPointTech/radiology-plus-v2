namespace RadiologyPlus.Scripting;

public enum ScriptLanguage
{
    /// <summary>T-SQL — Microsoft SQL Server.</summary>
    Tsql = 1,

    /// <summary>PL/pgSQL — PostgreSQL.</summary>
    Pgsql = 2,

    /// <summary>PowerShell 7+.</summary>
    PowerShell = 3,

    /// <summary>Windows Batch (cmd.exe).</summary>
    Batch = 4,
}

public static class ScriptLanguageParser
{
    public static ScriptLanguage Parse(string token) => token.ToLowerInvariant() switch
    {
        "tsql" or "sql" or "mssql" => ScriptLanguage.Tsql,
        "pgsql" or "postgres" or "postgresql" or "plpgsql" => ScriptLanguage.Pgsql,
        "powershell" or "ps" or "pwsh" => ScriptLanguage.PowerShell,
        "batch" or "cmd" or "bat" => ScriptLanguage.Batch,
        _ => throw new ArgumentException($"Unknown script language: {token}", nameof(token)),
    };

    public static string ToDbToken(this ScriptLanguage lang) => lang switch
    {
        ScriptLanguage.Tsql => "tsql",
        ScriptLanguage.Pgsql => "pgsql",
        ScriptLanguage.PowerShell => "powershell",
        ScriptLanguage.Batch => "batch",
        _ => throw new ArgumentOutOfRangeException(nameof(lang)),
    };
}
