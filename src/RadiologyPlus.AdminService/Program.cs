using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using RadiologyPlus.Common.Encryption;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.Identity;
using RadiologyPlus.Core.Tenancy;
using RadiologyPlus.Data.Connections;
using RadiologyPlus.Data.Notifications;
using RadiologyPlus.Data.Scripting;
using RadiologyPlus.Notifications;
using RadiologyPlus.Scripting;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddWindowsService(o => o.ServiceName = "RadiologyPlus.AdminService");
    builder.Services.AddSerilog((sp, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

    // Options
    builder.Services.Configure<AppDbOptions>(opts =>
        opts.ConnectionString = builder.Configuration.GetConnectionString("AppDb") ?? "");

    // Ambient accessors
    builder.Services.AddSingleton<ITenantContextAccessor, AsyncLocalTenantContextAccessor>();
    builder.Services.AddSingleton<ICurrentUser, AsyncLocalCurrentUser>();

    // Encryption
    builder.Services.AddSingleton<IEncryptionService>(_ =>
    {
        var key = builder.Configuration["Encryption:Key"]
            ?? throw new InvalidOperationException("Encryption:Key is required.");
        return new AesGcmEncryptionService(key);
    });

    // Data layer
    builder.Services.AddSingleton<IAppDbContext, AppDbContext>();
    builder.Services.AddSingleton<IScriptRepository, ScriptRepository>();
    builder.Services.AddSingleton<INotificationRepository, NotificationRepository>();

    // Scripting + notifications
    builder.Services.AddRadiologyPlusScripting(
        maxConcurrent: builder.Configuration.GetValue<int?>("Service:MaxConcurrentScripts") ?? 5);
    builder.Services.AddRadiologyPlusNotifications(builder.Configuration);

    // Hosted services — the technical workers relocated out of RadiologyPlus.Service.
    builder.Services.AddHostedService<ScriptScheduler>();
    builder.Services.AddNotificationOrchestratorHostedService();

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex) when (ex is InvalidOperationException or HostAbortedException)
{
    Log.Fatal(ex, "AdminService terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}
