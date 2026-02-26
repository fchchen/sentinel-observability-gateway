using Microsoft.AspNetCore.SignalR;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Realtime.Hub.Contracts;
using Realtime.Hub.Observability;

namespace Realtime.Hub;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSignalR();
        builder.Services.AddCors(options =>
        {
            options.AddPolicy("local-dev", policy =>
            {
                policy.SetIsOriginAllowed(origin => origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase))
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .AllowCredentials();
            });
        });

        var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"] ?? "http://localhost:4317";
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(RealtimeTelemetry.ServiceName))
            .WithTracing(tracing => tracing
                .AddSource(RealtimeTelemetry.ActivitySourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlpEndpoint)))
            .WithMetrics(metrics => metrics
                .AddMeter(RealtimeTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlpEndpoint)));

        var app = builder.Build();
        app.UseCors("local-dev");

        app.MapGet("/", () => Results.Ok(new { service = "realtime-hub", status = "ok" }));
        app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "realtime-hub" }));
        app.MapGet("/metrics", () => Results.Text(
            "# TYPE realtime_bootstrap_up gauge\nrealtime_bootstrap_up 1\n",
            "text/plain; version=0.0.4"));
        app.MapPost("/v1/realtime/publish", async (
            RealtimePublishRequest payload,
            IHubContext<Hubs.EventStreamHub> hubContext,
            CancellationToken cancellationToken) =>
        {
            using var broadcastActivity = RealtimeTelemetry.ActivitySource.StartActivity("signalr.broadcast", System.Diagnostics.ActivityKind.Producer);
            broadcastActivity?.SetTag("event.id", payload.EventId);
            broadcastActivity?.SetTag("tenant.id", payload.TenantId);
            broadcastActivity?.SetTag("event.type", payload.Type);
            broadcastActivity?.SetTag("streamKey", payload.StreamKey);
            await hubContext.Clients.All.SendAsync("eventReceived", payload, cancellationToken);
            return Results.Accepted();
        });
        app.MapHub<Hubs.EventStreamHub>("/hubs/events");

        await app.RunAsync();
    }
}
