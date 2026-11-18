using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Rockets.Api.Configuration;
using Rockets.Api.Domain;
using Rockets.Api.Events;
using Rockets.Api.Repositories;
using Rockets.Api.Services;
using Testcontainers.PostgreSql;

namespace Rockets.Tests.Support;

/// <summary>
/// One real Postgres (in Docker, via Testcontainers) shared by all tests. Each test uses its own channels, so tests
/// don't see each other's rockets. Pulsar isn't needed here: the tests call the processor the worker would call.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
    private readonly List<NpgsqlDataSource> _instances = [];
    private NpgsqlDataSource _shared = null!;

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _shared = NpgsqlDataSource.Create(ConnectionString);
        await NewRepository().InitializeAsync();
        await NewOutbox().InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var dataSource in _instances.Append(_shared))
        {
            await dataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>
    /// Connections for a repository: the shared pool, or with <paramref name="separateInstance"/> a pool of its own,
    /// like another service instance. (A new pool per call would exhaust Postgres' 100 connections.)
    /// </summary>
    private NpgsqlDataSource Connections(bool separateInstance)
    {
        if (!separateInstance)
        {
            return _shared;
        }

        var dataSource = NpgsqlDataSource.Create(ConnectionString);
        lock (_instances)
        {
            _instances.Add(dataSource);
        }

        return dataSource;
    }

    public RocketRepository NewRepository(bool separateInstance = false) => new(Connections(separateInstance));

    public OutboxRepository NewOutbox(bool separateInstance = false) => new(Connections(separateInstance));

    /// <summary>The worker's processor (with <paramref name="separateInstance"/>: as another service instance).</summary>
    public RocketEventProcessor NewProcessor(
        int maxPendingMessages = 1_000, IRocketRepository? repository = null, bool separateInstance = false) => new(
        repository ?? NewRepository(separateInstance),
        Options.Create(new RocketOptions { MaxPendingMessagesPerRocket = maxPendingMessages }),
        new RocketMetrics(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()),
        NullLogger<RocketEventProcessor>.Instance);

    public static string NewChannel(string label) => $"{label}-{Guid.NewGuid():N}";

    /// <summary>What the rocket should look like: the same messages applied once, in order.</summary>
    public static RocketSnapshot Expected(string channel, IEnumerable<RocketMessage> messages)
    {
        var rocket = new Rocket(channel, maxPendingMessages: 10_000);
        foreach (var message in messages.OrderBy(m => m.MessageNumber))
        {
            rocket.Receive(message);
        }

        return rocket.Snapshot();
    }

    public async Task<long> CountOutboxAsync(string channel)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM outbox WHERE channel = $1", connection);
        command.Parameters.AddWithValue(channel);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    public async Task<long> CountEventsAsync(string channel)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM events WHERE channel = $1", connection);
        command.Parameters.AddWithValue(channel);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
