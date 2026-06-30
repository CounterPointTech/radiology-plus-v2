using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using RadiologyPlus.API.Auth;
using RadiologyPlus.API.Endpoints;
using RadiologyPlus.Common.Encryption;
using RadiologyPlus.Common.Security;
using RadiologyPlus.API.Hubs;
using RadiologyPlus.Core.Announcements;
using RadiologyPlus.Core.Audit;
using RadiologyPlus.Core.Billing;
using RadiologyPlus.Core.Data;
using RadiologyPlus.Core.Identity;
using RadiologyPlus.Core.TechValidation;
using RadiologyPlus.Core.Tenancy;
using RadiologyPlus.Data.Announcements;
using RadiologyPlus.Data.Audit;
using RadiologyPlus.Data.Billing;
using RadiologyPlus.Data.Connections;
using RadiologyPlus.Data.Identity;
using RadiologyPlus.Data.Tenancy;
using RadiologyPlus.Data.TechValidation;
using RadiologyPlus.NovaradAuth;
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
builder.Services.AddScoped<INovaradWriter, NovaradWriter>();

// Tech Validation (Phase 1)
builder.Services.AddScoped<ITechValidationRepository, TechValidationRepository>();
builder.Services.AddScoped<INovaradStudyReader, NovaradStudyReader>();
builder.Services.AddScoped<IDoTheDoOrchestrator, DoTheDoOrchestrator>();
builder.Services.AddScoped<IStudyMergeService, StudyMergeService>();
builder.Services.AddScoped<IFfiComparisonSink, NoOpFfiComparisonSink>();

// Billing (Phase 2)
builder.Services.AddScoped<IBillingRepository, BillingRepository>();
builder.Services.AddSingleton<ICptMasterImporter, CptMasterImporter>();
builder.Services.AddSingleton<IRvuValuesImporter, RvuValuesImporter>();          // item 1.2 — CMS PPRRVU parser (stateless)
builder.Services.AddScoped<IRvuWriteBackSink, MModalRvuWriteBackSink>();          // M*Modal RVU write-back (self-gates on tenancy.mmodal_connections; NoOpRvuWriteBackSink is the hard-off alternative)
builder.Services.AddScoped<INovaradReportsReader, NovaradReportsReader>();
builder.Services.AddSingleton<IReconciliationExporter, ReconciliationExporter>();

// Announcements (admin status banner)
builder.Services.AddScoped<IAnnouncementsRepository, AnnouncementsRepository>();

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
        // Pass JWT for SignalR connections via access_token query string
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    ctx.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddOpenApi();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:3000"])
    .AllowAnyMethod()
    .AllowAnyHeader()
    .AllowCredentials()));
builder.Services.AddSignalR();
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
app.MapTechValidationEndpoints();
app.MapBillingEndpoints();
app.MapAnnouncementsEndpoints();
app.MapHub<MonitoringHub>("/hubs/monitoring");

await app.RunAsync();
