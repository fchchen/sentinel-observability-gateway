using Query.Api.Data;
using Query.Api.Observability;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Query.Api;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<QueryStore>();

        var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"] ?? "http://localhost:4317";
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(QueryTelemetry.ServiceName))
            .WithTracing(tracing => tracing
                .AddSource(QueryTelemetry.ActivitySourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlpEndpoint)))
            .WithMetrics(metrics => metrics
                .AddMeter(QueryTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlpEndpoint))
                .AddPrometheusExporter());

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<QueryStore>();
            await store.EnsureSchemaAsync(CancellationToken.None);
        }

        app.MapGet("/", () => Results.Ok(new { service = "query-api", status = "ok" }));
        app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "query-api" }));
        app.UseOpenTelemetryPrometheusScrapingEndpoint();
        app.MapGet("/v1/pipeline/health", async (QueryStore store, CancellationToken cancellationToken) =>
        {
            var snapshot = await store.GetPipelineHealthAsync(cancellationToken);
            return Results.Ok(snapshot);
        });
        app.MapGet("/v1/events/recent", async (QueryStore store, string? tenantId, int? limit, CancellationToken cancellationToken) =>
        {
            var safeLimit = Math.Clamp(limit ?? 100, 1, 500);
            var rows = await store.GetRecentEventsAsync(tenantId, safeLimit, cancellationToken);
            return Results.Ok(rows);
        });

        await app.RunAsync();
    }
}
