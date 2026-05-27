namespace RadiologyPlus.Scripting;

public sealed class ScriptExecutorFactory
{
    private readonly Dictionary<ScriptLanguage, IScriptExecutor> _byLanguage;

    public ScriptExecutorFactory(IEnumerable<IScriptExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        _byLanguage = executors.ToDictionary(e => e.Language);
    }

    public IScriptExecutor Get(ScriptLanguage language)
    {
        if (_byLanguage.TryGetValue(language, out var ex)) return ex;
        throw new NotSupportedException($"No executor registered for {language}.");
    }

    public bool Supports(ScriptLanguage language) => _byLanguage.ContainsKey(language);

    public IReadOnlyCollection<ScriptLanguage> SupportedLanguages => _byLanguage.Keys;
}
