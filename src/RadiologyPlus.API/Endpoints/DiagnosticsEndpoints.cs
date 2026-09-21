namespace RadiologyPlus.API.Endpoints;

public static class DiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/diagnostics").WithTags("Diagnostics");

        group.MapGet("/version", () => Results.Ok(new
        {
            Product = "Radiology Plus",
            Version = typeof(DiagnosticsEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown",
            // Local time, like the rest of the app; only the audit log is UTC.
            Time = DateTimeOffset.Now,
        }));

        return app;
    }
}
