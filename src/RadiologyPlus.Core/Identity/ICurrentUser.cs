namespace RadiologyPlus.Core.Identity;

public interface ICurrentUser
{
    AppUser? Current { get; }
    AppUser Require();
    IDisposable Push(AppUser user);
}

public sealed class AsyncLocalCurrentUser : ICurrentUser
{
    private static readonly AsyncLocal<AppUser?> _current = new();

    public AppUser? Current => _current.Value;

    public AppUser Require() =>
        _current.Value ?? throw new InvalidOperationException(
            "No user in scope. Middleware should populate this from the JWT.");

    public IDisposable Push(AppUser user)
    {
        var previous = _current.Value;
        _current.Value = user;
        return new Restore(previous);
    }

    private sealed class Restore(AppUser? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
