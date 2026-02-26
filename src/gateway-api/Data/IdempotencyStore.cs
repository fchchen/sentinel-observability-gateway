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
        const string insertSql = """
            INSERT INTO idempotency (tenant_id, idempotency_key, payload_hash, first_seen_utc)
            VALUES (@tenant_id, @idempotency_key, @payload_hash, now())
            ON CONFLICT (tenant_id, idempotency_key) DO NOTHING;
            """;

        const string lookupSql = """
            SELECT payload_hash
            FROM idempotency
            WHERE tenant_id = @tenant_id AND idempotency_key = @idempotency_key;
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using (var insert = new NpgsqlCommand(insertSql, connection))
        {
            insert.Parameters.AddWithValue("tenant_id", tenantId);
            insert.Parameters.AddWithValue("idempotency_key", idempotencyKey);
            insert.Parameters.AddWithValue("payload_hash", payloadHash);
            var rows = await insert.ExecuteNonQueryAsync(cancellationToken);
            if (rows == 1)
            {
                return IdempotencyInsertResult.Inserted;
            }
        }

        await using var lookup = new NpgsqlCommand(lookupSql, connection);
        lookup.Parameters.AddWithValue("tenant_id", tenantId);
        lookup.Parameters.AddWithValue("idempotency_key", idempotencyKey);
        var existingHash = (string?)await lookup.ExecuteScalarAsync(cancellationToken);

        if (existingHash is null)
        {
            return IdempotencyInsertResult.Conflict;
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
