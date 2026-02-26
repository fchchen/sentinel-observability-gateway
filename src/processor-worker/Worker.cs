using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using Processor.Worker.Contracts;
using Processor.Worker.Data;
using Processor.Worker.Observability;
using Processor.Worker.Options;

namespace Processor.Worker;

public class Worker(
    ILogger<Worker> logger,
    IOptions<WorkerOptions> optionsAccessor,
    WorkerStore store,
    WorkerMetrics metrics,
    IHttpClientFactory httpClientFactory) : BackgroundService
{
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = optionsAccessor.Value;
        if (string.IsNullOrWhiteSpace(options.KafkaBootstrapServers))
        {
            throw new InvalidOperationException("Missing Worker:KafkaBootstrapServers configuration.");
        }

        if (string.IsNullOrWhiteSpace(options.KafkaTopic))
        {
            throw new InvalidOperationException("Missing Worker:KafkaTopic configuration.");
        }

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = options.KafkaBootstrapServers,
            GroupId = options.KafkaGroupId,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
            AllowAutoCreateTopics = true
        };

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
        consumer.Subscribe(options.KafkaTopic);
        logger.LogInformation("Worker subscribed to {Topic} as group {GroupId}", options.KafkaTopic, options.KafkaGroupId);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string>? result;
            try
            {
                result = consumer.Consume(TimeSpan.FromSeconds(1));
            }
            catch (ConsumeException ex)
            {
                logger.LogError(ex, "Kafka consume error");
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (result is null)
            {
                continue;
            }

            var parentContext = ExtractPropagationContext(result.Message.Headers);
            Baggage.Current = parentContext.Baggage;
            using var consumeActivity = WorkerTelemetry.ActivitySource.StartActivity(
                "kafka.consume",
                ActivityKind.Consumer,
                parentContext.ActivityContext);
            consumeActivity?.SetTag("messaging.system", "kafka");
            consumeActivity?.SetTag("messaging.destination", options.KafkaTopic);
            consumeActivity?.SetTag("messaging.kafka.partition", result.Partition.Value);
            consumeActivity?.SetTag("messaging.kafka.offset", result.Offset.Value);

            var outcome = await ProcessMessageAsync(result.Message.Value, options, stoppingToken);
            if (outcome == ProcessOutcome.Retry)
            {
                continue;
            }

            try
            {
                consumer.Commit(result);
            }
            catch (KafkaException ex)
            {
                logger.LogError(ex, "Failed to commit offset for topic {Topic} partition {Partition} offset {Offset}",
                    result.Topic, result.Partition.Value, result.Offset.Value);
            }
        }

        consumer.Close();
    }

    private async Task<ProcessOutcome> ProcessMessageAsync(
        string rawMessage,
        WorkerOptions options,
        CancellationToken cancellationToken)
    {
        KafkaEventMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<KafkaEventMessage>(rawMessage, _jsonOptions);
            if (message is null)
            {
                return await MoveToDeadLetterAsync(
                    tenantId: null,
                    snapshot: rawMessage,
                    reason: "Kafka message deserialized as null",
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            return await MoveToDeadLetterAsync(
                tenantId: null,
                snapshot: rawMessage,
                reason: $"Invalid message JSON: {ex.Message}",
                cancellationToken);
        }

        if (!Guid.TryParse(message.EventId, out _))
        {
            return await MoveToDeadLetterAsync(
                message.TenantId,
                rawMessage,
                "eventId is not a valid UUID",
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(message.TenantId) ||
            string.IsNullOrWhiteSpace(message.Source) ||
            string.IsNullOrWhiteSpace(message.Type) ||
            string.IsNullOrWhiteSpace(message.StreamKey) ||
            string.IsNullOrWhiteSpace(message.IdempotencyKey))
        {
            return await MoveToDeadLetterAsync(
                message.TenantId,
                rawMessage,
                "One or more required fields are missing",
                cancellationToken);
        }

        PersistResult result;
        try
        {
            using var persistActivity = WorkerTelemetry.ActivitySource.StartActivity("db.persist", ActivityKind.Client);
            persistActivity?.SetTag("db.system", "postgresql");
            persistActivity?.SetTag("tenant.id", message.TenantId);
            persistActivity?.SetTag("event.id", message.EventId);
            result = await store.PersistAsync(message, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist event {EventId}", message.EventId);
            return await MoveToDeadLetterAsync(message.TenantId, rawMessage, ex.Message, cancellationToken);
        }

        var nowUtc = DateTimeOffset.UtcNow;
        metrics.RecordLag((nowUtc - message.TimestampUtc).TotalSeconds);
        metrics.RecordFreshness((nowUtc - message.ReceivedAtUtc).TotalSeconds);

        if (result == PersistResult.Duplicate)
        {
            logger.LogInformation("Duplicate event skipped: {EventId}", message.EventId);
            metrics.RecordSuccess();
            return ProcessOutcome.Commit;
        }

        var published = await PublishRealtimeAsync(message, options.RealtimePublishUrl, cancellationToken);
        if (!published)
        {
            logger.LogWarning("Failed to publish event {EventId} to realtime hub", message.EventId);
        }

        metrics.RecordSuccess();
        return ProcessOutcome.Commit;
    }

    private async Task<ProcessOutcome> MoveToDeadLetterAsync(
        string? tenantId,
        string snapshot,
        string reason,
        CancellationToken cancellationToken)
    {
        using var dlqActivity = WorkerTelemetry.ActivitySource.StartActivity("db.dlq.write", ActivityKind.Client);
        dlqActivity?.SetTag("db.system", "postgresql");
        dlqActivity?.SetTag("tenant.id", tenantId ?? "unknown");

        var written = await store.WriteDeadLetterAsync(tenantId, snapshot, reason, cancellationToken);
        if (!written)
        {
            logger.LogError("Failed to write dead-letter record. message will be retried.");
            metrics.RecordRetry();
            return ProcessOutcome.Retry;
        }

        logger.LogWarning("Message moved to dead-letter. tenant={TenantId}, reason={Reason}", tenantId, reason);
        metrics.RecordDlq();
        return ProcessOutcome.Commit;
    }

    private async Task<bool> PublishRealtimeAsync(
        KafkaEventMessage message,
        string publishUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(publishUrl))
        {
            return false;
        }

        var client = httpClientFactory.CreateClient("realtime");
        using var publishActivity = WorkerTelemetry.ActivitySource.StartActivity("realtime.publish", ActivityKind.Producer);
        publishActivity?.SetTag("http.url", publishUrl);
        publishActivity?.SetTag("event.id", message.EventId);
        publishActivity?.SetTag("tenant.id", message.TenantId);

        var payload = new RealtimePublishRequest(
            EventId: message.EventId,
            TenantId: message.TenantId,
            Source: message.Source,
            Type: message.Type,
            TimestampUtc: message.TimestampUtc,
            StreamKey: message.StreamKey,
            ReceivedAtUtc: message.ReceivedAtUtc,
            ProcessedAtUtc: DateTimeOffset.UtcNow,
            TraceId: message.TraceId);

        try
        {
            var response = await client.PostAsJsonAsync(publishUrl, payload, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static PropagationContext ExtractPropagationContext(Headers headers)
    {
        return Propagator.Extract(default, headers, static (carrier, key) =>
        {
            if (carrier.TryGetLastBytes(key, out var bytes) && bytes is { Length: > 0 })
            {
                return new[] { Encoding.UTF8.GetString(bytes) };
            }

            return Array.Empty<string>();
        });
    }

    private enum ProcessOutcome
    {
        Commit,
        Retry
    }
}
