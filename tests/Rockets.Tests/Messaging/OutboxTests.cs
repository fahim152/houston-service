using System.Collections.Concurrent;
using Rockets.Tests.Support;
using static Rockets.Tests.Support.TestMessages;

namespace Rockets.Tests.Messaging;

/// <summary>
/// The outbox: messages stored while Pulsar was down are relayed to Pulsar later, oldest first, each row by exactly
/// one instance (SELECT … FOR UPDATE SKIP LOCKED). Pulsar is replaced by a recording function.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OutboxTests(PostgresFixture db)
{
    // Pulsar is back: rows are published oldest first and removed from the outbox.
    [Fact]
    public async Task Rows_are_published_oldest_first_and_then_removed()
    {
        var channel = PostgresFixture.NewChannel("relay");
        var outbox = db.NewOutbox();
        for (var n = 1; n <= 5; n++)
        {
            await outbox.AddAsync(channel, n, ToJson(Increased(n, 1, channel)));
        }

        var published = new List<long>();
        while (await outbox.RelayBatchAsync((ch, body) => Record(published, ch, channel, body), batchSize: 2) > 0)
        {
        }

        Assert.Equal([1L, 2, 3, 4, 5], published);
        Assert.Equal(0, await db.CountOutboxAsync(channel));
    }

    // Pulsar still down: publishing fails, the transaction rolls back, and every row stays for the next attempt.
    [Fact]
    public async Task When_publishing_fails_the_rows_stay_in_the_outbox()
    {
        var channel = PostgresFixture.NewChannel("relay-fail");
        var outbox = db.NewOutbox();
        for (var n = 1; n <= 3; n++)
        {
            await outbox.AddAsync(channel, n, ToJson(Increased(n, 1, channel)));
        }

        await Assert.ThrowsAsync<TimeoutException>(() => outbox.RelayBatchAsync(
            (ch, _) => ch == channel ? throw new TimeoutException("Pulsar did not answer") : Task.CompletedTask, batchSize: 10));

        Assert.Equal(3, await db.CountOutboxAsync(channel));
    }

    // Scale-out: three instances relay at the same time. The row locks make each row go to exactly one
    // instance, so nothing is published twice.
    [Fact]
    public async Task Three_instances_relaying_at_once_publish_each_row_exactly_once()
    {
        var channel = PostgresFixture.NewChannel("relay-scale");
        var setup = db.NewOutbox();
        for (var n = 1; n <= 300; n++)
        {
            await setup.AddAsync(channel, n, ToJson(Increased(n, 1, channel)));
        }

        var published = new ConcurrentBag<long>();
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            var instance = db.NewOutbox(separateInstance: true);   // own connection pool, like another service instance
            while (await instance.RelayBatchAsync(async (ch, body) =>
                   {
                       if (ch == channel)
                       {
                           published.Add(Number(body));
                           await Task.Delay(1);   
                       }
                   }, batchSize: 10) > 0)
            {
            }
        })));

        Assert.Equal(300, published.Count);
        Assert.Equal(300, published.Distinct().Count());
        Assert.Equal(0, await db.CountOutboxAsync(channel));
    }

    private static Task Record(List<long> published, string channel, string testChannel, string body)
    {
        if (channel == testChannel)
        {
            published.Add(Number(body));
        }

        return Task.CompletedTask;
    }

    private static long Number(string body) =>
        System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("metadata").GetProperty("messageNumber").GetInt64();
}
