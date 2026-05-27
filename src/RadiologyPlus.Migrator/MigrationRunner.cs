using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Serilog;

namespace RadiologyPlus.Migrator;

internal sealed class MigrationRunner
{
    private readonly string _connectionString;

    public MigrationRunner(string connectionString) => _connectionString = connectionString;

    public async Task<int> RunAsync()
    {
        var migrationsDir = Path.Combine(AppContext.BaseDirectory, "Migrations");
        if (!Directory.Exists(migrationsDir))
        {
            Log.Error("Migrations directory not found: {Dir}", migrationsDir);
            return 1;
        }

        var migrationFiles = Directory
            .GetFiles(migrationsDir, "*.sql")
            .OrderBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (migrationFiles.Count == 0)
        {
            Log.Warning("No migration files found in {Dir}.", migrationsDir);
            return 0;
        }

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        Log.Information("Connected to {Host}/{Database}", conn.Host, conn.Database);

        await EnsureMigrationsTableAsync(conn);

        var applied = await LoadAppliedVersionsAsync(conn);
        Log.Information("{Count} migrations already applied.", applied.Count);

        var newCount = 0;
        foreach (var file in migrationFiles)
        {
            var version = Path.GetFileNameWithoutExtension(file);
            var sql = await File.ReadAllTextAsync(file);
            var checksum = Sha256(sql);

            if (applied.TryGetValue(version, out var existing))
            {
                if (existing != checksum && existing != "manual")
                {
                    Log.Warning("Migration {Version} checksum drift: stored={Stored} current={Current}",
                        version, existing, checksum);
                }
                continue;
            }

            Log.Information("Applying {Version}...", version);
            await using var tx = await conn.BeginTransactionAsync();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.CommandTimeout = 300;
                await cmd.ExecuteNonQueryAsync();

                await using var update = conn.CreateCommand();
                update.Transaction = tx;
                update.CommandText = "UPDATE core.schema_migrations SET checksum = @c WHERE version = @v";
                update.Parameters.AddWithValue("c", checksum);
                update.Parameters.AddWithValue("v", version);
                await update.ExecuteNonQueryAsync();

                await tx.CommitAsync();
                Log.Information("  ✓ {Version} applied", version);
                newCount++;
            }
            catch (Exception ex)
            {
                try { await tx.RollbackAsync(); }
                catch (InvalidOperationException) { /* tx already completed by inner SQL */ }
                Log.Error(ex, "Migration {Version} failed; rolled back.", version);
                return 2;
            }
        }

        Log.Information("Done. {New} new migration(s) applied.", newCount);
        return 0;
    }

    private static async Task EnsureMigrationsTableAsync(NpgsqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE SCHEMA IF NOT EXISTS core;
            CREATE TABLE IF NOT EXISTS core.schema_migrations (
                version    TEXT PRIMARY KEY,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                checksum   TEXT NOT NULL,
                applied_by TEXT NOT NULL DEFAULT CURRENT_USER
            );
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<Dictionary<string, string>> LoadAppliedVersionsAsync(NpgsqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version, checksum FROM core.schema_migrations";
        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }

    private static string Sha256(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
