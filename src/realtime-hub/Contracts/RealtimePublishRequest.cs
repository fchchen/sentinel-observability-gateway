namespace Realtime.Hub.Contracts;

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
