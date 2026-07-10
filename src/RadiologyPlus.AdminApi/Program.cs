using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using RadiologyPlus.AdminApi.Endpoints;
using RadiologyPlus.Common.Encryption;
using RadiologyPlus.Common.Security;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.Identity;
using RadiologyPlus.Core.Tenancy;
using RadiologyPlus.Data.Audit;
using RadiologyPlus.Data.Connections;
using RadiologyPlus.Data.Identity;
using RadiologyPlus.Data.Notifications;
using RadiologyPlus.Data.Scripting;
using RadiologyPlus.Data.Tenancy;
using RadiologyPlus.Notifications;
using RadiologyPlus.Notifications.Channels;
using RadiologyPlus.NovaradAuth;
using RadiologyPlus.Scripting;
using RadiologyPlus.WebShared.Auth;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, sp, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

// Bind options
builder.Services.Configure<AppDbOptions>(opts =>
    opts.ConnectionString = builder.Configuration.GetConnectionString("AppDb") ?? "");
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));

// Tenancy + identity ambient accessors
builder.Services.AddSingleton<ITenantContextAccessor, AsyncLocalTenantContextAccessor>();
builder.Services.AddSingleton<ICurrentUser, AsyncLocalCurrentUser>();

// Encryption + password hashing
builder.Services.AddSingleton<IEncryptionService>(_ =>
{
    var key = builder.Configuration["Encryption:Key"]
        ?? throw new InvalidOperationException("Encryption:Key is required.");
    return new AesGcmEncryptionService(key);
});
builder.Services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();

// Data + repositories
builder.Services.AddSingleton<IAppDbContext, AppDbContext>();
builder.Services.AddSingleton<INovaradDbContext, NovaradConnectionPool>();
builder.Services.AddScoped<TenantRepository>();
builder.Services.AddScoped<ITenantRepository>(sp => sp.GetRequiredService<TenantRepository>());
builder.Services.AddScoped<IIdentityRepository, IdentityRepository>();
builder.Services.AddNovaradFederatedAuth(builder.Configuration);
builder.Services.AddScoped<IAccessAuditWriter, AccessAuditWriter>();

// Scripting — the engine + executors power manual runs and the smoke test;
// the admin repositories back the Script Manager + Chains CRUD/history surfaces.
builder.Services.AddSingleton<IScriptRepository, ScriptRepository>();
builder.Services.AddSingleton<IScriptAdminRepository, ScriptAdminRepository>();
builder.Services.AddSingleton<IScriptConnectionResolver, ScriptConnectionResolver>();
builder.Services.AddSingleton<IScriptChainRepository, ScriptChainRepository>();
builder.Services.AddSingleton<IChainAdminRepository, ChainAdminRepository>();
builder.Services.AddSingleton<IChainFailureNotifier, ChainFailureNotifier>();
builder.Services.AddRadiologyPlusScripting(
    maxConcurrent: builder.Configuration.GetValue<int?>("Service:MaxConcurrentScripts") ?? 2);

// Notifications — queue/template management, compose, and the Graph settings surface.
// The orchestrator (actual sending) runs in RadiologyPlus.AdminService; this host only
// needs the service (queue/render), the channel (settings test), and the admin repo.
builder.Services.AddSingleton<INotificationRepository, NotificationRepository>();
builder.Services.AddSingleton<INotificationAdminRepository, NotificationAdminRepository>();
builder.Services.AddSingleton<IGraphEmailSettingsStore, GraphEmailSettingsStore>();
builder.Services.AddRadiologyPlusNotifications(builder.Configuration);

// JWT
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();

var jwtSecret = builder.Configuration["Jwt:Secret"] ?? "";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "RadiologyPlus";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "RadiologyPlus";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        // Preserve JWT registered claim names ("sub", "jti", ...) instead of mapping
        // them to ClaimTypes.* long forms — the middleware reads them by short name.
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddOpenApi();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:3100"])
    .AllowAnyMethod()
    .AllowAnyHeader()
    .AllowCredentials()));
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseSerilogRequestLogging();
app.UseCors();
app.UseAuthentication();
app.UseTenantAndUserContext();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapAuthEndpoints();
app.MapDiagnosticsEndpoints();
app.MapScriptsEndpoints();
app.MapChainsEndpoints();
app.MapNotificationsEndpoints();

await app.RunAsync();
