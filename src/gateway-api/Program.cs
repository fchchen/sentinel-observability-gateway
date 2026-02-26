using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Confluent.Kafka;
using Gateway.Api.Contracts;
using Gateway.Api.Data;
using Gateway.Api.Observability;
using Gateway.Api.Options;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Gateway.Api;

public class Program
{
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
        builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection(GatewayOptions.SectionName));
        builder.Services.AddSingleton<IdempotencyStore>();
        builder.Services.AddSingleton<GatewayMetrics>();
        builder.Services.AddSingleton<IProducer<string, string>>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GatewayOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.KafkaBootstrapServers))
            {
                throw new InvalidOperationException("Missing Gateway:KafkaBootstrapServers configuration.");
            }

            return new ProducerBuilder<string, string>(new ProducerConfig
            {
                BootstrapServers = options.KafkaBootstrapServers,
                Acks = Acks.All,
                EnableIdempotence = true
            }).Build();
        });

        var otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"] ?? "http://localhost:4317";
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(GatewayTelemetry.ServiceName))
            .WithTracing(tracing => tracing
                .AddSource(GatewayTelemetry.ActivitySourceName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlpEndpoint)))
            .WithMetrics(metrics => metrics
                .AddMeter(GatewayTelemetry.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlpEndpoint))
                .AddPrometheusExporter());

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IdempotencyStore>();
            var initLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            try
            {
                await store.EnsureSchemaAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                initLogger.LogCritical(ex, "Failed to initialize database schema — aborting startup");
                throw;
            }
        }

        app.MapGet("/", () => Results.Ok(new { service = "gateway-api", status = "ok" }));
        app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "gateway-api" }));
        app.UseOpenTelemetryPrometheusScrapingEndpoint();

        app.MapPost("/v1/events", async (
            HttpRequest request,
            EventEnvelope envelope,
            IdempotencyStore store,
            IProducer<string, string> producer,
            IOptions<GatewayOptions> optionsAccessor,
            GatewayMetrics metrics,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            var stopwatch = Stopwatch.StartNew();

            IResult Complete(IResult result, int statusCode)
            {
                metrics.RecordRequest(statusCode, stopwatch.Elapsed.TotalMilliseconds);
                return result;
            }

            if (!request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKeyValue) ||
                string.IsNullOrWhiteSpace(idempotencyKeyValue))
            {
                return Complete(
                    Results.BadRequest(new { error = "Missing required header: Idempotency-Key" }),
                    StatusCodes.Status400BadRequest);
            }

            if (string.IsNullOrWhiteSpace(envelope.EventId) ||
                string.IsNullOrWhiteSpace(envelope.TenantId) ||
                string.IsNullOrWhiteSpace(envelope.StreamKey) ||
                string.IsNullOrWhiteSpace(envelope.Type) ||
                string.IsNullOrWhiteSpace(envelope.Source))
            {
                return Complete(
                    Results.BadRequest(new { error = "eventId, tenantId, source, type, and streamKey are required" }),
                    StatusCodes.Status400BadRequest);
            }

            if (envelope.EventId.Length > 128 || envelope.TenantId.Length > 128 ||
                envelope.Source.Length > 256 || envelope.Type.Length > 256 ||
                envelope.StreamKey.Length > 256)
            {
                return Complete(
                    Results.BadRequest(new { error = "One or more fields exceed maximum allowed length" }),
                    StatusCodes.Status400BadRequest);
            }

            var idempotencyKey = idempotencyKeyValue.ToString();
            var payloadHash = ComputePayloadHash(envelope);
            var insertResult = await store.TryRegisterAsync(envelope.TenantId, idempotencyKey, payloadHash, cancellationToken);
            if (insertResult == IdempotencyInsertResult.Conflict)
            {
                return Complete(
                    Results.Conflict(new { error = "Idempotency key was reused with a different payload." }),
                    StatusCodes.Status409Conflict);
            }

            var receivedAtUtc = DateTimeOffset.UtcNow;
            var traceId = Activity.Current?.TraceId.ToString() ?? request.HttpContext.TraceIdentifier;
            if (insertResult == IdempotencyInsertResult.Duplicate)
            {
                return Complete(
                    Results.Accepted(value: new EventAcceptedResponse(envelope.EventId, receivedAtUtc, traceId, Duplicate: true)),
                    StatusCodes.Status202Accepted);
            }

            var options = optionsAccessor.Value;
            var message = new KafkaEventMessage(
                envelope.EventId,
                envelope.TenantId,
                envelope.Source,
                envelope.Type,
                envelope.TimestampUtc,
                envelope.SchemaVersion,
                envelope.StreamKey,
                envelope.Payload,
                idempotencyKey,
                payloadHash,
                receivedAtUtc,
                traceId);

            try
            {
                var payload = JsonSerializer.Serialize(message);
                var key = $"{envelope.TenantId}|{envelope.StreamKey}";
                var headers = new Headers();
                if (Activity.Current is not null)
                {
                    Propagator.Inject(
                        new PropagationContext(Activity.Current.Context, Baggage.Current),
                        headers,
                        static (carrier, propagationKey, propagationValue) =>
                            carrier.Add(propagationKey, Encoding.UTF8.GetBytes(propagationValue)));
                }

                using var produceActivity = GatewayTelemetry.ActivitySource.StartActivity("kafka.produce", ActivityKind.Producer);
                produceActivity?.SetTag("messaging.system", "kafka");
                produceActivity?.SetTag("messaging.destination", options.KafkaTopic);
                produceActivity?.SetTag("tenant.id", envelope.TenantId);
                produceActivity?.SetTag("event.id", envelope.EventId);
                produceActivity?.SetTag("event.type", envelope.Type);
                produceActivity?.SetTag("streamKey", envelope.StreamKey);
                produceActivity?.SetTag("idempotencyKey", idempotencyKey);

                await producer.ProduceAsync(
                    options.KafkaTopic,
                    new Message<string, string> { Key = key, Value = payload, Headers = headers },
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to publish event {EventId} to Kafka topic {Topic}", envelope.EventId, options.KafkaTopic);
                await store.DeleteAsync(envelope.TenantId, idempotencyKey, cancellationToken);
                return Complete(
                    Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
                    StatusCodes.Status503ServiceUnavailable);
            }

            return Complete(
                Results.Accepted(value: new EventAcceptedResponse(envelope.EventId, receivedAtUtc, traceId, Duplicate: false)),
                StatusCodes.Status202Accepted);
        }).WithMetadata(new RequestSizeLimitAttribute(256 * 1024)); // 256 KB, route-specific

        await app.RunAsync();
    }

    private static string ComputePayloadHash(EventEnvelope envelope)
    {
        var payload = JsonSerializer.Serialize(envelope);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
