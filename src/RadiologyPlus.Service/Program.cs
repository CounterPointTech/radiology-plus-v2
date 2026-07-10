using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using RadiologyPlus.Common.Encryption;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.Identity;
using RadiologyPlus.Core.TechValidation;
using RadiologyPlus.Core.Tenancy;
using RadiologyPlus.Data.Audit;
using RadiologyPlus.Data.Connections;
using RadiologyPlus.Data.Identity;
using RadiologyPlus.Data.Tenancy;
using RadiologyPlus.Data.TechValidation;
using RadiologyPlus.Service.TechValidation;
using RadiologyPlus.Service.Workers;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddWindowsService(o => o.ServiceName = "RadiologyPlus.Service");
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
    builder.Services.AddSingleton<INovaradDbContext, NovaradConnectionPool>();

    // Tenants / identity (needed by Tech Validation projector for the per-tenant loop)
    builder.Services.AddScoped<TenantRepository>();
    builder.Services.AddScoped<ITenantRepository>(sp => sp.GetRequiredService<TenantRepository>());
    builder.Services.AddScoped<IIdentityRepository, IdentityRepository>();
    builder.Services.AddScoped<IAccessAuditWriter, AccessAuditWriter>();
    builder.Services.AddScoped<INovaradWriter, NovaradWriter>();

    // Tech Validation (Phase 1)
    builder.Services.Configure<ReadyStudiesProjectorOptions>(builder.Configuration.GetSection("TechValidation"));
    builder.Services.AddScoped<ITechValidationRepository, TechValidationRepository>();
    builder.Services.AddScoped<INovaradStudyReader, NovaradStudyReader>();
    builder.Services.AddScoped<IDoTheDoOrchestrator, DoTheDoOrchestrator>();

    // Hosted services — clinical only. ScriptScheduler + NotificationOrchestrator
    // moved to RadiologyPlus.AdminService (the technical/clinical split).
    builder.Services.AddHostedService<ReadyStudiesProjector>();
    builder.Services.AddHostedService<HeartbeatWorker>();

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex) when (ex is InvalidOperationException or HostAbortedException)
{
    Log.Fatal(ex, "Service terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}
