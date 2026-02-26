using Processor.Worker;
using Processor.Worker.Data;
using Processor.Worker.Observability;
using Processor.Worker.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection(WorkerOptions.SectionName));
builder.Services.AddHttpClient("realtime");
builder.Services.AddSingleton<WorkerStore>();
builder.Services.AddSingleton<WorkerMetrics>();

var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"] ?? "http://localhost:4317";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(WorkerTelemetry.ServiceName))
    .WithTracing(tracing => tracing
        .AddSource(WorkerTelemetry.ActivitySourceName)
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlpEndpoint)))
    .WithMetrics(metrics => metrics
        .AddMeter(WorkerTelemetry.MeterName)
        .AddRuntimeInstrumentation()
        .AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlpEndpoint)));

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
using (var scope = host.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<WorkerStore>();
    await store.EnsureSchemaAsync(CancellationToken.None);
}

await host.RunAsync();
