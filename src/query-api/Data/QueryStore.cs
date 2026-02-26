using Npgsql;
using NpgsqlTypes;

namespace Query.Api.Data;

public sealed record PipelineHealthSnapshot(
    string Gateway,
    string Worker,
    double LagSeconds,
    long DlqEvents,
    long ProcessedEvents,
    double ThroughputPerMinute);

public sealed record RecentEventView(
    Guid Id,
    string TenantId,
    string Source,
    string Type,
    string StreamKey,
    DateTimeOffset TimestampUtc,
    DateTimeOffset ReceivedUtc,
    DateTimeOffset ProcessedUtc,
    string PayloadJson);

public sealed class QueryStore(IConfiguration configuration)
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

            CREATE TABLE IF NOT EXISTS dead_letter (
                id UUID PRIMARY KEY,
                tenant_id TEXT NULL,
                event_snapshot_jsonb JSONB NOT NULL,
                reason TEXT NOT NULL,
                created_utc TIMESTAMPTZ NOT NULL
            );
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PipelineHealthSnapshot> GetPipelineHealthAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
              COALESCE((SELECT COUNT(*)::BIGINT FROM dead_letter), 0) AS dlq_events,
              COALESCE((SELECT COUNT(*)::BIGINT FROM events), 0) AS processed_events,
              COALESCE((SELECT EXTRACT(EPOCH FROM (now() - MAX(processed_utc)))::DOUBLE PRECISION FROM events), 0) AS lag_seconds,
              COALESCE((SELECT COUNT(*)::DOUBLE PRECISION FROM events WHERE processed_utc >= now() - interval '1 minute'), 0) AS throughput_per_minute;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return new PipelineHealthSnapshot("unknown", "unknown", 0, 0, 0, 0);
        }

        var dlqEvents = reader.GetInt64(reader.GetOrdinal("dlq_events"));
        var processedEvents = reader.GetInt64(reader.GetOrdinal("processed_events"));
        var lagSeconds = reader.GetDouble(reader.GetOrdinal("lag_seconds"));
        var throughputPerMinute = reader.GetDouble(reader.GetOrdinal("throughput_per_minute"));

        return new PipelineHealthSnapshot(
            Gateway: "unknown",
            Worker: "unknown",
            LagSeconds: Math.Round(Math.Max(lagSeconds, 0), 2),
            DlqEvents: dlqEvents,
            ProcessedEvents: processedEvents,
            ThroughputPerMinute: Math.Round(Math.Max(throughputPerMinute, 0), 2));
    }

    public async Task<IReadOnlyList<RecentEventView>> GetRecentEventsAsync(string? tenantId, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, tenant_id, source, type, stream_key, timestamp_utc, received_utc, processed_utc, payload_jsonb
            FROM events
            WHERE (@tenant_id IS NULL OR tenant_id = @tenant_id)
            ORDER BY processed_utc DESC
            LIMIT @limit;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add("tenant_id", NpgsqlDbType.Text).Value = (object?)tenantId ?? DBNull.Value;
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<RecentEventView>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new RecentEventView(
                Id: reader.GetGuid(reader.GetOrdinal("id")),
                TenantId: reader.GetString(reader.GetOrdinal("tenant_id")),
                Source: reader.GetString(reader.GetOrdinal("source")),
                Type: reader.GetString(reader.GetOrdinal("type")),
                StreamKey: reader.GetString(reader.GetOrdinal("stream_key")),
                TimestampUtc: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("timestamp_utc")),
                ReceivedUtc: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("received_utc")),
                ProcessedUtc: reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("processed_utc")),
                PayloadJson: reader.GetString(reader.GetOrdinal("payload_jsonb"))));
        }

        return rows;
    }
}
