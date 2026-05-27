using System.Security.Cryptography;
using Npgsql;
using RadiologyPlus.Common.Security;
using Serilog;

namespace RadiologyPlus.Migrator;

internal sealed class CreateNrsCommand
{
    private const string TempPasswordAlphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    private readonly string _connectionString;

    public CreateNrsCommand(string connectionString) => _connectionString = connectionString;

    public async Task<int> RunAsync(IReadOnlyDictionary<string, string> flags)
    {
        var tenantCode = flags.Required("tenant");
        var username = flags.Required("username");
        var displayName = flags.Optional("display-name", username);
        var email = flags.Optional("email", "");
        var providedPassword = flags.Optional("password", "");

        var temporaryPassword = string.IsNullOrEmpty(providedPassword)
            ? GenerateTempPassword(16)
            : providedPassword;

        var hasher = new BCryptPasswordHasher();
        var hash = hasher.Hash(temporaryPassword);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        // Resolve tenant_id
        Guid tenantId;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT tenant_id FROM tenancy.tenants WHERE code = @c";
            cmd.Parameters.AddWithValue("c", tenantCode);
            var result = await cmd.ExecuteScalarAsync();
            if (result is null or DBNull)
            {
                throw new UsageException(
                    $"No tenant found with code '{tenantCode}'. Run 'init-tenant' first.");
            }
            tenantId = (Guid)result;
        }

        // Insert NRS user. role=1 = NRS (see RadiologyPlus.Core.Identity.Role).
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO identity.users
                    (tenant_id, username, display_name, email, role, is_local, password_hash, is_active)
                VALUES
                    (@t, @u, @dn, @e, 1, TRUE, @h, TRUE)
                ON CONFLICT (tenant_id, username) DO UPDATE SET
                    display_name = EXCLUDED.display_name,
                    email = EXCLUDED.email,
                    role = EXCLUDED.role,
                    is_local = TRUE,
                    password_hash = EXCLUDED.password_hash,
                    is_active = TRUE,
                    updated_at = NOW()
                """;
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("u", username);
            cmd.Parameters.AddWithValue("dn", displayName);
            cmd.Parameters.AddWithValue("e", (object?)(string.IsNullOrEmpty(email) ? null : email) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("h", hash);
            await cmd.ExecuteNonQueryAsync();
        }

        Log.Information("NRS user '{User}' ready for tenant {Tenant}.", username, tenantCode);

        if (string.IsNullOrEmpty(providedPassword))
        {
            Console.WriteLine();
            Console.WriteLine("============================================================");
            Console.WriteLine("  Temporary NRS password (copy now, will not be shown again):");
            Console.WriteLine($"  {temporaryPassword}");
            Console.WriteLine("============================================================");
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine($"NRS user '{username}' password set to the value you supplied.");
        }

        return 0;
    }

    private static string GenerateTempPassword(int length)
    {
        var buffer = new char[length];
        var bytes = new byte[length * 4];
        RandomNumberGenerator.Fill(bytes);
        for (var i = 0; i < length; i++)
        {
            var idx = BitConverter.ToUInt32(bytes, i * 4) % (uint)TempPasswordAlphabet.Length;
            buffer[i] = TempPasswordAlphabet[(int)idx];
        }
        return new string(buffer);
    }
}
