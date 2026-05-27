namespace RadiologyPlus.Core.Tenancy;

public sealed record TenantContext(Guid TenantId, string TenantCode, string TenantName);

public interface ITenantContextAccessor
{
    TenantContext? Current { get; }
    TenantContext Require();
    IDisposable Push(TenantContext tenant);
}

public sealed class AsyncLocalTenantContextAccessor : ITenantContextAccessor
{
    private static readonly AsyncLocal<TenantContext?> _current = new();

    public TenantContext? Current => _current.Value;

    public TenantContext Require() =>
        _current.Value ?? throw new InvalidOperationException(
            "No tenant in scope. Call Push() or ensure middleware resolved the tenant from the request.");

    public IDisposable Push(TenantContext tenant)
    {
        var previous = _current.Value;
        _current.Value = tenant;
        return new Restore(previous);
    }

    private sealed class Restore(TenantContext? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
