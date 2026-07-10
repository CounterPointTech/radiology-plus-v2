using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using RadiologyPlus.Core.Identity;

namespace RadiologyPlus.WebShared.Auth;

public sealed class JwtOptions
{
    public string Issuer { get; init; } = "RadiologyPlus";
    public string Audience { get; init; } = "RadiologyPlus";
    public string Secret { get; init; } = "";
    public int AccessTokenMinutes { get; init; } = 60;
    public int RefreshTokenDays { get; init; } = 14;
}

public interface IJwtTokenService
{
    AccessTokenResult IssueAccessToken(AppUser user);
    string IssueRefreshTokenRaw();
    string HashRefreshToken(string raw);
}

public sealed record AccessTokenResult(string Token, DateTimeOffset ExpiresAt, int ExpiresInSeconds);

public sealed class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _options;

    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.Secret) || _options.Secret.Length < 32)
            throw new InvalidOperationException("Jwt:Secret must be at least 32 characters.");
    }

    public AccessTokenResult IssueAccessToken(AppUser user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.UserId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("tid", user.TenantId.ToString()),
            new("name", user.Username),
            new("dn", user.DisplayName),
            new(ClaimTypes.Role, user.Role.ToString()),
        };
        foreach (var fid in user.FacilityIds) claims.Add(new Claim("fid", fid.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var jwt = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: creds);

        var token = new JwtSecurityTokenHandler().WriteToken(jwt);
        return new AccessTokenResult(token, expires, _options.AccessTokenMinutes * 60);
    }

    public string IssueRefreshTokenRaw()
    {
        var bytes = RandomNumberGenerator.GetBytes(48);
        return Convert.ToBase64String(bytes);
    }

    public string HashRefreshToken(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToBase64String(bytes);
    }
}
