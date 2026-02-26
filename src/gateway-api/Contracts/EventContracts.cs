using System.Text.Json;

namespace Gateway.Api.Contracts;

public sealed record EventEnvelope(
    string EventId,
    string TenantId,
    string Source,
    string Type,
    DateTimeOffset TimestampUtc,
    int SchemaVersion,
    string StreamKey,
    JsonElement Payload);

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

public sealed record EventAcceptedResponse(string EventId, DateTimeOffset ReceivedAtUtc, string TraceId, bool Duplicate);
