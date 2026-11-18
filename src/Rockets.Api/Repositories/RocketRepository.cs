using Npgsql;
using Rockets.Api.Domain;
using Rockets.Api.Enums;

namespace Rockets.Api.Repositories;

/// <summary>
/// Two tables:
/// <list type="bullet">
/// <item><c>events</c> — the event store: every accepted event, kept for history. The primary key
/// <c>(channel, message_number)</c> stores each event once.</item>
/// <item><c>rocket_state</c> — each rocket's current state (a snapshot). Its row is also the per-rocket lock.</item>
/// </list>
/// </summary>
public sealed class RocketRepository(NpgsqlDataSource dataSource) : IRocketRepository
{
    private const string StateColumns =
        "channel, type, speed, mission, status, explosion_reason, launched_at, last_updated_at, last_applied";

    // Waiting events = stored events numbered after the last applied one.
    private const string WaitingCount =
        "(SELECT count(*) FROM events e WHERE e.channel = s.channel AND e.message_number > s.last_applied)::int";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            CREATE TABLE IF NOT EXISTS events (
                channel        TEXT        NOT NULL,
                message_number BIGINT      NOT NULL,
                body           TEXT        NOT NULL,
                stored_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (channel, message_number)
            );
            CREATE TABLE IF NOT EXISTS rocket_state (
                channel          TEXT PRIMARY KEY,
                type             TEXT,
                speed            BIGINT      NOT NULL DEFAULT 0,
                mission          TEXT,
                status           TEXT        NOT NULL DEFAULT 'AwaitingLaunch',
                explosion_reason TEXT,
                launched_at      TIMESTAMPTZ,
                last_updated_at  TIMESTAMPTZ,
                last_applied     BIGINT      NOT NULL DEFAULT 0
            );
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IRocketTransaction> LockAsync(string channel, CancellationToken cancellationToken = default)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken);

            // Make sure the row exists, then lock it. Another worker locking the same rocket waits here until we finish.
            await using (var insert = new NpgsqlCommand(
                "INSERT INTO rocket_state (channel) VALUES ($1) ON CONFLICT (channel) DO NOTHING", connection, transaction))
            {
                insert.Parameters.AddWithValue(channel);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var select = new NpgsqlCommand(
                $"SELECT {StateColumns}, 0 FROM rocket_state WHERE channel = $1 FOR UPDATE", connection, transaction);
            select.Parameters.AddWithValue(channel);
            RocketSnapshot state;
            await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            {
                await reader.ReadAsync(cancellationToken);
                state = ReadState(reader);
            }

            return new Transaction(connection, transaction, state);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<RocketSnapshot?> FindAsync(string channel, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {StateColumns}, {WaitingCount} FROM rocket_state s WHERE channel = $1");
        command.Parameters.AddWithValue(channel);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadState(reader) : null;
    }

    public async Task<IReadOnlyList<RocketSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand($"SELECT {StateColumns}, {WaitingCount} FROM rocket_state s");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var states = new List<RocketSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            states.Add(ReadState(reader));
        }

        return states;
    }

    private static RocketSnapshot ReadState(NpgsqlDataReader reader) => new(
        Channel: reader.GetString(0),
        Type: reader.IsDBNull(1) ? null : reader.GetString(1),
        Speed: reader.GetInt64(2),
        Mission: reader.IsDBNull(3) ? null : reader.GetString(3),
        Status: Enum.Parse<RocketStatus>(reader.GetString(4)),
        ExplosionReason: reader.IsDBNull(5) ? null : reader.GetString(5),
        LaunchedAt: reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        LastUpdatedAt: reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
        LastMessageNumber: reader.GetInt64(8),
        PendingMessages: reader.GetInt32(9));

    private sealed class Transaction(NpgsqlConnection connection, NpgsqlTransaction transaction, RocketSnapshot state)
        : IRocketTransaction
    {
        public RocketSnapshot State { get; } = state;

        public async Task<IReadOnlyList<string>> GetWaitingEventsAsync(CancellationToken cancellationToken = default)
        {
            await using var command = new NpgsqlCommand(
                "SELECT body FROM events WHERE channel = $1 AND message_number > $2 ORDER BY message_number",
                connection, transaction);
            command.Parameters.AddWithValue(State.Channel);
            command.Parameters.AddWithValue(State.LastMessageNumber);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var bodies = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                bodies.Add(reader.GetString(0));
            }

            return bodies;
        }

        public async Task SaveAsync(
            string channel, long messageNumber, string body, RocketSnapshot newState, CancellationToken cancellationToken = default)
        {
            await using (var insert = new NpgsqlCommand(
                "INSERT INTO events (channel, message_number, body) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING",
                connection, transaction))
            {
                insert.Parameters.AddWithValue(channel);
                insert.Parameters.AddWithValue(messageNumber);
                insert.Parameters.AddWithValue(body);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = new NpgsqlCommand("""
                UPDATE rocket_state SET type = $2, speed = $3, mission = $4, status = $5, explosion_reason = $6,
                                        launched_at = $7, last_updated_at = $8, last_applied = $9
                 WHERE channel = $1
                """, connection, transaction);
            command.Parameters.AddWithValue(channel);
            command.Parameters.AddWithValue((object?)newState.Type ?? DBNull.Value);
            command.Parameters.AddWithValue(newState.Speed);
            command.Parameters.AddWithValue((object?)newState.Mission ?? DBNull.Value);
            command.Parameters.AddWithValue(newState.Status.ToString());
            command.Parameters.AddWithValue((object?)newState.ExplosionReason ?? DBNull.Value);
            // timestamptz stores an instant; Npgsql only accepts it in UTC (reads also come back in UTC).
            command.Parameters.AddWithValue((object?)newState.LaunchedAt?.ToUniversalTime() ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)newState.LastUpdatedAt?.ToUniversalTime() ?? DBNull.Value);
            command.Parameters.AddWithValue(newState.LastMessageNumber);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public Task CommitAsync(CancellationToken cancellationToken = default) => transaction.CommitAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();   // rolls back if not committed, releasing the row lock
            await connection.DisposeAsync();
        }
    }
}
