using Npgsql;

namespace Gateway.Api.Data;

public enum IdempotencyInsertResult
{
    Inserted,
    Duplicate,
    Conflict
}

public sealed class IdempotencyStore(IConfiguration configuration)
{
    private readonly string _connectionString = configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("Missing connection string: ConnectionStrings:Postgres");

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS idempotency (
                tenant_id TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                payload_hash TEXT NOT NULL,
                first_seen_utc TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (tenant_id, idempotency_key)
            );
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IdempotencyInsertResult> TryRegisterAsync(
        string tenantId,
        string idempotencyKey,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        // ON CONFLICT DO UPDATE ensures the conflicting transaction blocks until the
        // winner commits, then returns the existing row — atomic under READ COMMITTED.
        // The no-op SET (existing value) creates a dead tuple on conflict, but this is
        // the correct tradeoff: conflicts are the minority path, and the alternative
        // (CTE with DO NOTHING) has a visibility gap under concurrency.
        const string sql = """
            INSERT INTO idempotency (tenant_id, idempotency_key, payload_hash, first_seen_utc)
            VALUES (@tenant_id, @idempotency_key, @payload_hash, now())
            ON CONFLICT (tenant_id, idempotency_key)
            DO UPDATE SET first_seen_utc = idempotency.first_seen_utc
            RETURNING payload_hash, (xmax = 0) AS inserted;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        command.Parameters.AddWithValue("payload_hash", payloadHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return IdempotencyInsertResult.Conflict;
        }

        var existingHash = reader.GetString(0);
        var inserted = reader.GetBoolean(1);

        if (inserted)
        {
            return IdempotencyInsertResult.Inserted;
        }

        return string.Equals(existingHash, payloadHash, StringComparison.Ordinal)
            ? IdempotencyInsertResult.Duplicate
            : IdempotencyInsertResult.Conflict;
    }

    public async Task DeleteAsync(string tenantId, string idempotencyKey, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM idempotency
            WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
