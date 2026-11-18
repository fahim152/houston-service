using Npgsql;

namespace Rockets.Api.Repositories;

/// <summary>The <c>outbox</c> table in Postgres: messages accepted while Pulsar was unavailable, oldest first.</summary>
public sealed class OutboxRepository(NpgsqlDataSource dataSource) : IOutboxRepository
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            CREATE TABLE IF NOT EXISTS outbox (
                id             BIGSERIAL PRIMARY KEY,
                channel        TEXT        NOT NULL,
                message_number BIGINT      NOT NULL,
                body           TEXT        NOT NULL,
                created_at     TIMESTAMPTZ NOT NULL DEFAULT now()
            )
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddAsync(string channel, long messageNumber, string body, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO outbox (channel, message_number, body) VALUES ($1, $2, $3)");
        command.Parameters.AddWithValue(channel);
        command.Parameters.AddWithValue(messageNumber);
        command.Parameters.AddWithValue(body);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> RelayBatchAsync(
        Func<string, string, Task> publish, int batchSize, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Row locks: another instance's relay skips these rows instead of publishing them too.
        var rows = new List<(long Id, string Channel, string Body)>();
        await using (var select = new NpgsqlCommand(
            "SELECT id, channel, body FROM outbox ORDER BY id LIMIT $1 FOR UPDATE SKIP LOCKED", connection, transaction))
        {
            select.Parameters.AddWithValue(batchSize);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        if (rows.Count == 0)
        {
            return 0;
        }

        foreach (var row in rows)
        {
            await publish(row.Channel, row.Body);   // throws → rollback: the rows stay and are retried
        }

        await using (var delete = new NpgsqlCommand("DELETE FROM outbox WHERE id = ANY($1)", connection, transaction))
        {
            delete.Parameters.AddWithValue(rows.Select(r => r.Id).ToArray());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        // A crash between publishing and this commit republishes the batch: the worker treats the copies as duplicates.
        await transaction.CommitAsync(cancellationToken);
        return rows.Count;
    }
}
