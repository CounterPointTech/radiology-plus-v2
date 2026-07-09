namespace RadiologyPlus.AdminApi.Endpoints;

public static class DiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/diagnostics").WithTags("Diagnostics");

        group.MapGet("/version", () => Results.Ok(new
        {
            Product = "Radiology Plus Admin",
            Version = typeof(DiagnosticsEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown",
            Time = DateTimeOffset.UtcNow,
        }));

        return app;
    }
}
