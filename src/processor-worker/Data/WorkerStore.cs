using Npgsql;
using NpgsqlTypes;
using Processor.Worker.Contracts;

namespace Processor.Worker.Data;

public enum PersistResult
{
    Processed,
    Duplicate
}

public sealed class WorkerStore(IConfiguration configuration, ILogger<WorkerStore> logger)
{
    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("Missing connection string: ConnectionStrings:Postgres");

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS events (
                id UUID PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                source TEXT NOT NULL,
                type TEXT NOT NULL,
                stream_key TEXT NOT NULL,
                timestamp_utc TIMESTAMPTZ NOT NULL,
                payload_jsonb JSONB NOT NULL,
                received_utc TIMESTAMPTZ NOT NULL,
                processed_utc TIMESTAMPTZ NOT NULL,
                trace_id TEXT,
                idempotency_key TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_events_tenant_timestamp ON events (tenant_id, timestamp_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_events_tenant_source_timestamp ON events (tenant_id, source, timestamp_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_events_tenant_type_timestamp ON events (tenant_id, type, timestamp_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_events_tenant_stream_timestamp ON events (tenant_id, stream_key, timestamp_utc DESC);

            CREATE TABLE IF NOT EXISTS stream_state (
                tenant_id TEXT NOT NULL,
                stream_key TEXT NOT NULL,
                last_seen_utc TIMESTAMPTZ NOT NULL,
                last_type TEXT NOT NULL,
                last_payload_jsonb JSONB NOT NULL,
                PRIMARY KEY (tenant_id, stream_key)
            );

            CREATE TABLE IF NOT EXISTS dead_letter (
                id UUID PRIMARY KEY,
                tenant_id TEXT NULL,
                event_snapshot_jsonb JSONB NOT NULL,
                reason TEXT NOT NULL,
                created_utc TIMESTAMPTZ NOT NULL
            );

            CREATE TABLE IF NOT EXISTS processed_events (
                event_id UUID PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                processed_utc TIMESTAMPTZ NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_processed_events_tenant_idempotency
                ON processed_events (tenant_id, idempotency_key);
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PersistResult> PersistAsync(KafkaEventMessage message, CancellationToken cancellationToken)
    {
        var eventId = Guid.Parse(message.EventId);
        var processedAtUtc = DateTimeOffset.UtcNow;

        const string dedupSql = """
            INSERT INTO processed_events (event_id, tenant_id, idempotency_key, processed_utc)
            VALUES (@event_id, @tenant_id, @idempotency_key, @processed_utc)
            ON CONFLICT DO NOTHING;
            """;

        const string eventsSql = """
            INSERT INTO events (
                id, tenant_id, source, type, stream_key, timestamp_utc,
                payload_jsonb, received_utc, processed_utc, trace_id, idempotency_key)
            VALUES (
                @id, @tenant_id, @source, @type, @stream_key, @timestamp_utc,
                @payload_jsonb, @received_utc, @processed_utc, @trace_id, @idempotency_key);
            """;

        const string streamStateSql = """
            INSERT INTO stream_state (tenant_id, stream_key, last_seen_utc, last_type, last_payload_jsonb)
            VALUES (@tenant_id, @stream_key, @last_seen_utc, @last_type, @last_payload_jsonb)
            ON CONFLICT (tenant_id, stream_key)
            DO UPDATE SET
                last_seen_utc = EXCLUDED.last_seen_utc,
                last_type = EXCLUDED.last_type,
                last_payload_jsonb = EXCLUDED.last_payload_jsonb;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var dedup = new NpgsqlCommand(dedupSql, connection, transaction))
        {
            dedup.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, eventId);
            dedup.Parameters.AddWithValue("tenant_id", message.TenantId);
            dedup.Parameters.AddWithValue("idempotency_key", message.IdempotencyKey);
            dedup.Parameters.AddWithValue("processed_utc", NpgsqlDbType.TimestampTz, processedAtUtc);
            var inserted = await dedup.ExecuteNonQueryAsync(cancellationToken);
            if (inserted == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return PersistResult.Duplicate;
            }
        }

        var receivedAt = message.ReceivedAtUtc == default ? DateTimeOffset.UtcNow : message.ReceivedAtUtc;
        await using (var events = new NpgsqlCommand(eventsSql, connection, transaction))
        {
            events.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, eventId);
            events.Parameters.AddWithValue("tenant_id", message.TenantId);
            events.Parameters.AddWithValue("source", message.Source);
            events.Parameters.AddWithValue("type", message.Type);
            events.Parameters.AddWithValue("stream_key", message.StreamKey);
            events.Parameters.AddWithValue("timestamp_utc", NpgsqlDbType.TimestampTz, message.TimestampUtc);
            events.Parameters.AddWithValue("payload_jsonb", NpgsqlDbType.Jsonb, message.Payload.GetRawText());
            events.Parameters.AddWithValue("received_utc", NpgsqlDbType.TimestampTz, receivedAt);
            events.Parameters.AddWithValue("processed_utc", NpgsqlDbType.TimestampTz, processedAtUtc);
            events.Parameters.Add("trace_id", NpgsqlDbType.Text).Value = (object?)message.TraceId ?? DBNull.Value;
            events.Parameters.AddWithValue("idempotency_key", message.IdempotencyKey);
            await events.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var stream = new NpgsqlCommand(streamStateSql, connection, transaction))
        {
            stream.Parameters.AddWithValue("tenant_id", message.TenantId);
            stream.Parameters.AddWithValue("stream_key", message.StreamKey);
            stream.Parameters.AddWithValue("last_seen_utc", NpgsqlDbType.TimestampTz, message.TimestampUtc);
            stream.Parameters.AddWithValue("last_type", message.Type);
            stream.Parameters.AddWithValue("last_payload_jsonb", NpgsqlDbType.Jsonb, message.Payload.GetRawText());
            await stream.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return PersistResult.Processed;
    }

    public async Task<bool> WriteDeadLetterAsync(
        string? tenantId,
        string snapshot,
        string reason,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO dead_letter (id, tenant_id, event_snapshot_jsonb, reason, created_utc)
            VALUES (@id, @tenant_id, @event_snapshot_jsonb, @reason, @created_utc);
            """;

        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, Guid.NewGuid());
            command.Parameters.Add("tenant_id", NpgsqlDbType.Text).Value = (object?)tenantId ?? DBNull.Value;
            command.Parameters.AddWithValue("event_snapshot_jsonb", NpgsqlDbType.Jsonb, NormalizeSnapshot(snapshot));
            command.Parameters.AddWithValue("reason", reason.Length > 500 ? reason[..500] : reason);
            command.Parameters.AddWithValue("created_utc", NpgsqlDbType.TimestampTz, DateTimeOffset.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write dead-letter record for tenant {TenantId}", tenantId);
            return false;
        }
    }

    private static string NormalizeSnapshot(string snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot))
        {
            return """{"raw":null}""";
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(snapshot);
            if (document.RootElement.ValueKind is System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Array)
            {
                return snapshot;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not valid JSON — will be wrapped below
        }

        return System.Text.Json.JsonSerializer.Serialize(new { raw = snapshot });
    }
}
