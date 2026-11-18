using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Rockets.Api.Configuration;
using Rockets.Api.Controllers;
using Npgsql;
using Rockets.Api.Messaging;
using Rockets.Api.Repositories;
using Rockets.Tests.Support;
using static Rockets.Tests.Support.TestMessages;

namespace Rockets.Tests.Api;

/// <summary>
/// The ingest API over HTTP. Pulsar is replaced by a fake publisher (recording, or failing like an unreachable Pulsar),
/// and the background consumer is switched off; Postgres is real.
/// </summary>
[Collection(PostgresCollection.Name)]
public class IngestApiTests(PostgresFixture db)
{
    // The sender resends on any non-2xx. A valid message is published (keyed by channel) and answered 202 Accepted;
    // an invalid one can never succeed, so it is kept for inspection and also answered 202, never published.
    [Fact]
    public async Task Valid_messages_are_published_and_invalid_ones_are_kept_with_202()
    {
        var published = new FakePublisher();
        await using var app = NewApp(published);
        using var client = app.CreateClient();
        var channel = PostgresFixture.NewChannel("api");

        var valid = await client.PostAsync("/messages", Json(ToJson(Launched(channel: channel))));
        var invalid = await client.PostAsync("/messages", Json("""{"metadata":{"channel":"x"}}"""));

        Assert.Equal((HttpStatusCode.Accepted, "Accepted"), (valid.StatusCode, Result(valid)));
        Assert.Equal((HttpStatusCode.Accepted, "Unprocessable"), (invalid.StatusCode, Result(invalid)));
        Assert.Equal([channel], published.Keys);                                     // only the valid one, keyed by channel
        Assert.Contains("metadata", await client.GetStringAsync("/unprocessable-messages"));
    }

    // Pulsar down: the message is stored in the Postgres outbox instead, and still answered 202. The outbox relay
    // publishes it once Pulsar is back.
    [Fact]
    public async Task When_Pulsar_is_unavailable_the_message_is_stored_in_the_outbox()
    {
        await using var app = NewApp(new UnavailablePublisher());
        using var client = app.CreateClient();
        var channel = PostgresFixture.NewChannel("outbox");

        var response = await client.PostAsync("/messages", Json(ToJson(Launched(channel: channel))));

        Assert.Equal((HttpStatusCode.Accepted, "AcceptedToOutbox"), (response.StatusCode, Result(response)));
        Assert.Equal(1, await db.CountOutboxAsync(channel));
    }

    // Pulsar AND the outbox down: the message is not stored anywhere, so the sender must resend it. 503 + Retry-After
    // says "temporarily unavailable, try again" (not a 500, which means a bug, and not a 202, which would lose it).
    [Fact]
    public async Task When_Pulsar_and_the_outbox_are_unavailable_the_sender_gets_503_with_Retry_After()
    {
        await using var app = NewApp(new UnavailablePublisher(), new UnavailableOutbox());
        using var client = app.CreateClient();

        var response = await client.PostAsync("/messages", Json(ToJson(Launched(channel: PostgresFixture.NewChannel("down")))));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    private WebApplicationFactory<Program> NewApp(IRocketEventPublisher publisher, IOutboxRepository? outbox = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting($"{RocketOptions.SectionName}:{nameof(RocketOptions.PostgresConnectionString)}", db.ConnectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRocketEventPublisher>();
                services.AddSingleton(publisher);
                services.RemoveAll<IHostedService>();   // no Pulsar consumer or outbox relay in these tests
                if (outbox is not null)
                {
                    services.RemoveAll<IOutboxRepository>();
                    services.AddSingleton(outbox);
                }
            });
        });

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string Result(HttpResponseMessage response) =>
        response.Headers.GetValues(MessagesController.ResultHeader).Single();

    private sealed class UnavailablePublisher : IRocketEventPublisher
    {
        public Task PublishAsync(string channel, string body, CancellationToken cancellationToken = default) =>
            throw new TimeoutException("Pulsar did not answer");
    }

    private sealed class UnavailableOutbox : IOutboxRepository
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AddAsync(string channel, long messageNumber, string body, CancellationToken cancellationToken = default) =>
            throw new NpgsqlException("Postgres is down");

        public Task<int> RelayBatchAsync(Func<string, string, Task> publish, int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class FakePublisher : IRocketEventPublisher
    {
        private readonly ConcurrentQueue<string> _keys = new();

        public IReadOnlyList<string> Keys => _keys.ToArray();

        public Task PublishAsync(string channel, string body, CancellationToken cancellationToken = default)
        {
            _keys.Enqueue(channel);
            return Task.CompletedTask;
        }
    }
}
