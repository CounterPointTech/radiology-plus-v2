namespace RadiologyPlus.Scripting;

/// <summary>
/// Resolves a script's connection_target ('appdb' | 'novarad' | 'mmodal' | 'none')
/// into a live connection string at run time. Implemented in the data layer where
/// the tenant connection tables and encryption live.
/// </summary>
public interface IScriptConnectionResolver
{
    /// <summary>
    /// Returns the connection string for the target, or null for 'none'.
    /// Throws InvalidOperationException when the target needs a configured
    /// tenant connection that doesn't exist.
    /// </summary>
    Task<string?> ResolveAsync(Guid tenantId, string connectionTarget, CancellationToken cancellationToken = default);
}
