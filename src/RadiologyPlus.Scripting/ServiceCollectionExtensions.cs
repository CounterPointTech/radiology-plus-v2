using Microsoft.Extensions.DependencyInjection;
using RadiologyPlus.Scripting.Executors;

namespace RadiologyPlus.Scripting;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddRadiologyPlusScripting(this IServiceCollection services, int maxConcurrent = 5)
    {
        services.AddSingleton<IScriptExecutor, PostgresScriptExecutor>();
        services.AddSingleton<IScriptExecutor, SqlServerScriptExecutor>();
        services.AddSingleton<IScriptExecutor, PowerShellScriptExecutor>();
        services.AddSingleton<IScriptExecutor, BatchScriptExecutor>();
        services.AddSingleton<ScriptExecutorFactory>();
        services.AddSingleton(sp => new ScriptExecutionEngine(
            sp.GetRequiredService<ScriptExecutorFactory>(),
            sp.GetRequiredService<IScriptRepository>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ScriptExecutionEngine>>(),
            maxConcurrent));
        return services;
    }
}
