using System.Text.Json;

namespace Processor.Worker.Contracts;

public sealed record KafkaEventMessage(
    string EventId,
    string TenantId,
    string Source,
    string Type,
    DateTimeOffset TimestampUtc,
    int SchemaVersion,
    string StreamKey,
    JsonElement Payload,
    string IdempotencyKey,
    string PayloadHash,
    DateTimeOffset ReceivedAtUtc,
    string TraceId);

public sealed record RealtimePublishRequest(
    string EventId,
    string TenantId,
    string Source,
    string Type,
    DateTimeOffset TimestampUtc,
    string StreamKey,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset ProcessedAtUtc,
    string TraceId);
