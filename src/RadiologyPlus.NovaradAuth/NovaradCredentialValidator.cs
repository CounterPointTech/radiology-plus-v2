using System.Data.Common;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.Identity;
using RadiologyPlus.Core.Tenancy;

namespace RadiologyPlus.NovaradAuth;

/// <summary>
/// Federated Novarad credential validator. Replicates Novarad's password algorithm
/// (no Novarad DLLs) and runs all queries against the tenant's <c>shared.users</c>
/// table over the existing per-tenant Npgsql data source.
///
/// Policy:
///  - <c>anonymous = TRUE</c> or <c>is_visible = FALSE</c> → reject.
///  - <c>is_ldap_user = TRUE</c> or <c>use_ad_authentication = TRUE</c> → LDAP branch.
///  - <c>is_vendor = TRUE</c> → default role <see cref="Role.Tech"/>.
///  - MFA (<c>second_factor_data</c>) is not enforced in v1; documented as a known scope cut.
///  - Lockout shares state with Novarad's <c>failed_password_attempt_count</c> with a local
///    cap of <see cref="LocalLockoutThreshold"/>.
///  - On success we reset the counter and update <c>last_login_date</c>.
/// </summary>
public sealed class NovaradCredentialValidator : INovaradCredentialValidator
{
    public const int LocalLockoutThreshold = 5;

    private readonly INovaradDbContext _novaradDb;
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly ITenantRepository _tenants;
    private readonly INovaradPasswordHasher _hasher;
    private readonly INovaradLdapAuthenticator _ldap;
    private readonly IMemoryCache _cache;
    private readonly ILogger<NovaradCredentialValidator> _logger;

    public NovaradCredentialValidator(
        INovaradDbContext novaradDb,
        ITenantContextAccessor tenantAccessor,
        ITenantRepository tenants,
        INovaradPasswordHasher hasher,
        INovaradLdapAuthenticator ldap,
        IMemoryCache cache,
        ILogger<NovaradCredentialValidator> logger)
    {
        _novaradDb = novaradDb;
        _tenantAccessor = tenantAccessor;
        _tenants = tenants;
        _hasher = hasher;
        _ldap = ldap;
        _cache = cache;
        _logger = logger;
    }

    public async Task<NovaradCredentialResult> ValidateAsync(
        Guid tenantId, string username, string password, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return Fail("Username and password are required.");
        }

        // The Auth endpoint does not run our TenantAndUserMiddleware (no JWT yet at login),
        // so we set the AsyncLocal tenant ourselves for the duration of the validation.
        var tenantCtx = await ResolveTenantContextAsync(tenantId, cancellationToken);
        if (tenantCtx is null)
        {
            return Fail("Unknown tenant.");
        }

        using var _ = _tenantAccessor.Push(tenantCtx);

        // Pull the user row + auth-relevant flags in one go.
        NovaradUserAuthRow? row;
        try
        {
            row = await LoadUserAsync(username, cancellationToken);
        }
        catch (NpgsqlException ex)
        {
            _logger.LogError(ex, "Novarad lookup failed for tenant {Tenant} user {User}.", tenantId, username);
            return Fail("Could not reach Novarad to validate credentials. Please try again.");
        }

        if (row is null)
        {
            return Fail("Invalid username or password.");
        }

        if (row.Anonymous)
        {
            _logger.LogWarning("Rejecting login for anonymous Novarad account {User} (tenant {Tenant}).", username, tenantId);
            return Fail("This Novarad account is not permitted to use Radiology Plus.");
        }

        if (!row.IsVisible)
        {
            _logger.LogWarning("Rejecting login for hidden Novarad account {User} (tenant {Tenant}).", username, tenantId);
            return Fail("This Novarad account is not permitted to use Radiology Plus.");
        }

        if (row.AccountIsLocked || row.FailedPasswordAttemptCount >= LocalLockoutThreshold)
        {
            _logger.LogWarning(
                "Login blocked: Novarad account is locked (tenant {Tenant} user {User}, fails={Fails}, novaradLocked={Locked}).",
                tenantId, username, row.FailedPasswordAttemptCount, row.AccountIsLocked);
            // Ensure the local flag is set if we're tripping our own cap.
            if (!row.AccountIsLocked) await TryLockAccountAsync(row.UserId, cancellationToken);
            return Fail("Account is locked. Contact your Novarad administrator.");
        }

        // LDAP branch
        if (row.IsLdapUser || row.UseAdAuthentication)
        {
            var ldapResult = await _ldap.AuthenticateAsync(tenantCtx, row, password, cancellationToken);
            if (!ldapResult.IsValid)
            {
                await OnBadPasswordAsync(row.UserId, cancellationToken);
                return Fail(ldapResult.FailureReason ?? "LDAP authentication failed.");
            }

            var ldapRole = MapRole(row, await LoadRoleNameAsync(row.UserId, cancellationToken));
            var ldapFacilities = await LoadFacilityIdsAsync(row.UserId, cancellationToken);
            await OnGoodPasswordAsync(row.UserId, cancellationToken);
            return new NovaradCredentialResult(
                IsValid: true,
                DisplayName: ComposeDisplayName(row),
                Email: row.Email,
                MappedRole: ldapRole,
                FacilityIds: ldapFacilities,
                FailureReason: null);
        }

        // Hash branch
        var encryptionKey = row.PasswordFormat == 2
            ? await GetEncryptionKeyAsync(tenantId, cancellationToken)
            : null;

        var salt = _hasher.DecodeSalt(row.PasswordSalt);
        bool valid;
        try
        {
            valid = _hasher.Verify(password, row.StoredPassword ?? "", row.PasswordFormat ?? 0, salt, encryptionKey);
        }
        catch (NotSupportedException ex)
        {
            _logger.LogError(ex, "Unsupported password_format={Format} for user {User} (tenant {Tenant}).",
                row.PasswordFormat, username, tenantId);
            return Fail("Password format is not yet supported by Radiology Plus.");
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Malformed Novarad credentials row for user {User} (tenant {Tenant}).", username, tenantId);
            return Fail("Could not verify credentials due to a malformed Novarad record.");
        }

        if (!valid)
        {
            await OnBadPasswordAsync(row.UserId, cancellationToken);
            return Fail("Invalid username or password.");
        }

        await OnGoodPasswordAsync(row.UserId, cancellationToken);

        var roleName = await LoadRoleNameAsync(row.UserId, cancellationToken);
        var role = MapRole(row, roleName);
        var facilityIds = await LoadFacilityIdsAsync(row.UserId, cancellationToken);

        return new NovaradCredentialResult(
            IsValid: true,
            DisplayName: ComposeDisplayName(row),
            Email: row.Email,
            MappedRole: role,
            FacilityIds: facilityIds,
            FailureReason: null);
    }

    private static NovaradCredentialResult Fail(string reason) => new(
        IsValid: false,
        DisplayName: null,
        Email: null,
        MappedRole: Role.Tech,
        FacilityIds: Array.Empty<int>(),
        FailureReason: reason);

    private async Task<TenantContext?> ResolveTenantContextAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var current = _tenantAccessor.Current;
        if (current is not null && current.TenantId == tenantId) return current;

        var tenant = await _tenants.GetByIdAsync(tenantId, cancellationToken);
        if (tenant is null) return null;
        return new TenantContext(tenant.TenantId, tenant.Code, tenant.DisplayName);
    }

    private async Task<NovaradUserAuthRow?> LoadUserAsync(string username, CancellationToken cancellationToken)
    {
        await using var conn = (NpgsqlConnection)await _novaradDb.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT user_id, user_name, first_name, last_name, email,
                   password, password_salt, password_format,
                   account_is_locked, failed_password_attempt_count,
                   password_requires_change, anonymous, is_visible,
                   is_vendor, is_ldap_user, use_ad_authentication, domain
            FROM shared.users
            WHERE user_name = @u
            """;
        cmd.Parameters.Add(new NpgsqlParameter("u", username));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        return new NovaradUserAuthRow(
            UserId: reader.GetInt32(0),
            UserName: reader.GetString(1),
            FirstName: reader.IsDBNull(2) ? null : reader.GetString(2),
            LastName: reader.IsDBNull(3) ? null : reader.GetString(3),
            Email: reader.IsDBNull(4) ? null : reader.GetString(4),
            StoredPassword: reader.IsDBNull(5) ? null : reader.GetString(5),
            PasswordSalt: reader.IsDBNull(6) ? null : reader.GetString(6),
            PasswordFormat: reader.IsDBNull(7) ? null : reader.GetInt32(7),
            AccountIsLocked: reader.GetBoolean(8),
            FailedPasswordAttemptCount: reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
            PasswordRequiresChange: reader.GetBoolean(10),
            Anonymous: reader.GetBoolean(11),
            IsVisible: reader.GetBoolean(12),
            IsVendor: reader.GetBoolean(13),
            IsLdapUser: reader.GetBoolean(14),
            UseAdAuthentication: reader.GetBoolean(15),
            Domain: reader.IsDBNull(16) ? null : reader.GetString(16));
    }

    private async Task<string?> LoadRoleNameAsync(int userId, CancellationToken cancellationToken)
    {
        await using var conn = (NpgsqlConnection)await _novaradDb.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.role_name
            FROM shared.users_in_roles uir
            JOIN shared.roles r ON r.role_id = uir.role_id
            WHERE uir.user_id = @u
            ORDER BY r.role_id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("u", userId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : (string)result;
    }

    private async Task<IReadOnlyList<int>> LoadFacilityIdsAsync(int userId, CancellationToken cancellationToken)
    {
        await using var conn = (NpgsqlConnection)await _novaradDb.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT facility_id FROM shared.user_facilities WHERE user_id = @u";
        cmd.Parameters.AddWithValue("u", userId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var ids = new List<int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt32(0));
        }
        return ids;
    }

    private async Task<string?> GetEncryptionKeyAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var cacheKey = $"novarad-encryption-key:{tenantId}";
        if (_cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrEmpty(cached))
        {
            return cached;
        }

        await using var conn = (NpgsqlConnection)await _novaradDb.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM shared.settings WHERE name = 'EncryptionKey'";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
        {
            _logger.LogError("Novarad EncryptionKey setting is missing for tenant {Tenant}.", tenantId);
            return null;
        }

        var key = (string)result;
        _cache.Set(cacheKey, key, TimeSpan.FromHours(1));
        return key;
    }

    private async Task OnBadPasswordAsync(int userId, CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = (NpgsqlConnection)await _novaradDb.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE shared.users
                SET failed_password_attempt_count = COALESCE(failed_password_attempt_count, 0) + 1,
                    failed_password_attempt_date = NOW()
                WHERE user_id = @u
                """;
            cmd.Parameters.AddWithValue("u", userId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (DbException ex)
        {
            // Increment failures are best-effort. We must not let a stat-tracking failure
            // mask the underlying bad-password result that's already on the way back.
            _logger.LogWarning(ex, "Failed to increment Novarad failure counter for user {UserId}.", userId);
        }
    }

    private async Task OnGoodPasswordAsync(int userId, CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = (NpgsqlConnection)await _novaradDb.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE shared.users
                SET failed_password_attempt_count = 0,
                    last_login_date = NOW()
                WHERE user_id = @u
                """;
            cmd.Parameters.AddWithValue("u", userId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (DbException ex)
        {
            _logger.LogWarning(ex, "Failed to reset Novarad failure counter / update last_login_date for user {UserId}.", userId);
        }
    }

    private async Task TryLockAccountAsync(int userId, CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = (NpgsqlConnection)await _novaradDb.OpenAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE shared.users SET account_is_locked = TRUE WHERE user_id = @u";
            cmd.Parameters.AddWithValue("u", userId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (DbException ex)
        {
            _logger.LogWarning(ex, "Failed to set account_is_locked for user {UserId}.", userId);
        }
    }

    /// <summary>
    /// Map a Novarad role name onto Radiology Plus's coarser role enum.
    /// Conservative defaults: unknown roles fall through to <see cref="Role.Tech"/>.
    /// Vendor flag (per decisions.md) wins when no explicit role row exists.
    /// </summary>
    internal static Role MapRole(NovaradUserAuthRow row, string? novaradRoleName)
    {
        if (!string.IsNullOrWhiteSpace(novaradRoleName))
        {
            // Novarad role names vary by site; use case-insensitive substring heuristics.
            var n = novaradRoleName.Trim();
            if (n.Contains("Radiologist", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Reading", StringComparison.OrdinalIgnoreCase))
                return Role.Radiologist;
            if (n.Contains("Admin", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Manager", StringComparison.OrdinalIgnoreCase))
                return Role.Admin;
            if (n.Contains("Tech", StringComparison.OrdinalIgnoreCase))
                return Role.Tech;
        }

        // No Novarad role row matched (or none assigned). Conservative default: Tech.
        // is_vendor=TRUE explicitly falls into the same bucket per decisions.md.
        return Role.Tech;
    }

    private static string ComposeDisplayName(NovaradUserAuthRow row)
    {
        var first = row.FirstName?.Trim();
        var last = row.LastName?.Trim();
        if (!string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(last)) return $"{first} {last}";
        if (!string.IsNullOrEmpty(last)) return last;
        if (!string.IsNullOrEmpty(first)) return first;
        return row.UserName;
    }
}

public sealed record NovaradUserAuthRow(
    int UserId,
    string UserName,
    string? FirstName,
    string? LastName,
    string? Email,
    string? StoredPassword,
    string? PasswordSalt,
    int? PasswordFormat,
    bool AccountIsLocked,
    int FailedPasswordAttemptCount,
    bool PasswordRequiresChange,
    bool Anonymous,
    bool IsVisible,
    bool IsVendor,
    bool IsLdapUser,
    bool UseAdAuthentication,
    string? Domain);
