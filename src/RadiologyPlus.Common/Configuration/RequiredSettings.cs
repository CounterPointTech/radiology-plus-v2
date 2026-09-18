using System.Text;
using Microsoft.Extensions.Configuration;

namespace RadiologyPlus.Common.Configuration;

/// <summary>
/// Fail-fast validation of the settings every host needs before it can do anything
/// safely. Called once, synchronously, at the top of each Program.cs — before the
/// container is built — so a misconfigured host dies at startup with one readable
/// message instead of on the first request with an opaque exception.
///
/// This exists because the guards inside the DI factories only run on first
/// resolution, and because appsettings.json used to ship placeholder values that
/// satisfied those guards (a 56-character public "secret" signs a valid JWT).
/// </summary>
public static class RequiredSettings
{
    /// <summary>Minimum JWT secret length; matches <c>JwtTokenService</c>.</summary>
    public const int MinJwtSecretLength = 32;

    /// <summary>
    /// Validates the connection string, the encryption key and (optionally) the JWT
    /// secret. Throws a single <see cref="InvalidOperationException"/> listing every
    /// problem found so an operator fixes them in one round trip.
    /// </summary>
    /// <param name="config">The host's configuration.</param>
    /// <param name="requireJwt">True for hosts that issue or validate tokens.</param>
    public static void Validate(IConfiguration config, bool requireJwt)
    {
        ArgumentNullException.ThrowIfNull(config);
        var problems = new List<string>();

        var appDb = config.GetConnectionString("AppDb");
        if (string.IsNullOrWhiteSpace(appDb))
        {
            problems.Add("ConnectionStrings:AppDb is not set (env ConnectionStrings__AppDb).");
        }

        var key = config["Encryption:Key"];
        if (string.IsNullOrWhiteSpace(key))
        {
            problems.Add("Encryption:Key is not set (env Encryption__Key). It must be base64 of 16, 24 or 32 random bytes.");
        }
        else
        {
            try
            {
                var bytes = Convert.FromBase64String(key);
                if (bytes.Length is not (16 or 24 or 32))
                {
                    problems.Add($"Encryption:Key decodes to {bytes.Length} bytes; it must be 16, 24 or 32.");
                }
            }
            catch (FormatException)
            {
                problems.Add("Encryption:Key is not valid base64. Generate one with: openssl rand -base64 32");
            }
        }

        if (requireJwt)
        {
            var secret = config["Jwt:Secret"];
            if (string.IsNullOrWhiteSpace(secret))
            {
                problems.Add("Jwt:Secret is not set (env Jwt__Secret). Generate one with: openssl rand -base64 64");
            }
            else if (secret.Length < MinJwtSecretLength)
            {
                problems.Add($"Jwt:Secret is {secret.Length} characters; it must be at least {MinJwtSecretLength}.");
            }
            else if (secret.Contains("REPLACE", StringComparison.OrdinalIgnoreCase)
                     || secret.Contains("DO_NOT_USE", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("Jwt:Secret is a placeholder value. Generate a real one with: openssl rand -base64 64");
            }
        }

        if (problems.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Refusing to start: required configuration is missing or invalid.");
        foreach (var p in problems)
        {
            sb.Append("  - ").AppendLine(p);
        }
        sb.Append("Set these through environment variables (compose does this from the stack's env file) or user secrets; they are deliberately absent from appsettings.json.");
        throw new InvalidOperationException(sb.ToString());
    }
}
