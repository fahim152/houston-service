using System.Collections.Concurrent;
using Rockets.Api.Enums;
using Rockets.Tests.Support;
using static Rockets.Tests.Support.TestMessages;

namespace Rockets.Tests.Concurrency;

/// <summary>
/// Concurrency control is the Postgres row lock (SELECT … FOR UPDATE): one rocket is processed by one thread or
/// instance at a time, different rockets in parallel.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ConcurrencyTests(PostgresFixture db)
{
    // 1. The race the row lock prevents: 10 copies of the same event released at the same instant.
    //    Without the lock, two could both see "not applied yet" and report Applied twice.
    //    A race doesn't happen on every attempt, so it is repeated on 20 fresh rockets.
    [Fact]
    public async Task Simultaneous_copies_of_one_event_are_applied_exactly_once()
    {
        var processor = db.NewProcessor();
        for (var round = 0; round < 20; round++)
        {
            var channel = PostgresFixture.NewChannel("burst");
            await processor.ProcessAsync(ToJson(Launched(channel: channel, speed: 500)));

            var startTogether = new TaskCompletionSource();
            var copies = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
            {
                await startTogether.Task;
                return await processor.ProcessAsync(ToJson(Increased(2, 3000, channel)));
            })).ToList();
            startTogether.SetResult();
            var results = await Task.WhenAll(copies);

            Assert.Equal(1, results.Count(r => r == ReceiveResult.Applied));
            Assert.Equal(9, results.Count(r => r == ReceiveResult.Duplicate));
            Assert.Equal(3500, (await db.NewRepository().FindAsync(channel))!.Speed);
        }
    }

    // 2. Scale-out: three service instances (own connection pools) each process the WHOLE stream for 5 rockets,
    //    each in its own shuffled order, in parallel. Every event must be taken in exactly once, and every rocket
    //    must end up as if its events were applied once, in order.
    [Fact]
    public async Task Three_instances_processing_the_same_stream_apply_each_event_exactly_once()
    {
        var perRocket = Enumerable.Range(0, 5)
            .Select(i => PostgresFixture.NewChannel($"scale{i}"))
            .ToDictionary(channel => channel, channel => Sequence(channel, count: 100, seed: channel.Length));
        var stream = perRocket.Values.SelectMany(m => m).ToList();   // 500 events

        var results = new ConcurrentBag<ReceiveResult>();
        await Task.WhenAll(Enumerable.Range(1, 3).Select(instance => Task.Run(async () =>
        {
            var processor = db.NewProcessor(separateInstance: true);
            var order = stream.ToArray();
            new Random(instance).Shuffle(order);
            await Parallel.ForEachAsync(order, new ParallelOptions { MaxDegreeOfParallelism = 3 }, async (m, _) =>
                results.Add(await processor.ProcessAsync(ToJson(m))));
        })));

        Assert.Equal(500, results.Count(r => r is ReceiveResult.Applied or ReceiveResult.Buffered));
        Assert.Equal(1_000, results.Count(r => r is ReceiveResult.Duplicate));

        var repository = db.NewRepository();
        foreach (var (channel, messages) in perRocket)
        {
            Assert.Equal(PostgresFixture.Expected(channel, messages), await repository.FindAsync(channel));
            Assert.Equal(100, await db.CountEventsAsync(channel));
        }
    }

    // 3. The lock is per rocket: while rocket A is locked, rocket B is processed straight away, and A's own event
    //    waits until A's lock is released.
    [Fact]
    public async Task A_locked_rocket_does_not_block_other_rockets_but_its_own_events_wait()
    {
        var (a, b) = (PostgresFixture.NewChannel("locked"), PostgresFixture.NewChannel("free"));
        var processor = db.NewProcessor();
        await processor.ProcessAsync(ToJson(Launched(channel: a, speed: 0)));
        await processor.ProcessAsync(ToJson(Launched(channel: b, speed: 0)));

        Task<ReceiveResult> eventForA;
        await using (await db.NewRepository().LockAsync(a))                 // someone else holds rocket A
        {
            Assert.Equal(ReceiveResult.Applied,
                await processor.ProcessAsync(ToJson(Increased(2, 1, b))).WaitAsync(TimeSpan.FromSeconds(5)));

            eventForA = processor.ProcessAsync(ToJson(Increased(2, 1, a)));
            await Task.Delay(500);
            Assert.False(eventForA.IsCompleted);                            // waiting for A's lock
        }                                                                   // lock released

        Assert.Equal(ReceiveResult.Applied, await eventForA.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // 4. Readers never see a half-applied rocket: until the worker commits, GET /rockets still returns the old state
    //    (Postgres only shows committed data to other connections), and the read doesn't wait for the lock.
    [Fact]
    public async Task Readers_only_see_committed_state()
    {
        var channel = PostgresFixture.NewChannel("reader");
        await db.NewProcessor().ProcessAsync(ToJson(Launched(channel: channel, speed: 500)));
        var reader = db.NewRepository();

        await using (var transaction = await db.NewRepository().LockAsync(channel))
        {
            var newState = transaction.State with { Speed = 3500, LastMessageNumber = 2 };
            await transaction.SaveAsync(channel, 2, ToJson(Increased(2, 3000, channel)), newState);

            Assert.Equal(500, (await reader.FindAsync(channel))!.Speed);  // written but not committed: invisible
            await transaction.CommitAsync();
        }

        Assert.Equal(3500, (await reader.FindAsync(channel))!.Speed);
    }

    // 5. A hot rocket: 200 events for ONE rocket, shuffled, with duplicates, processed by 8 threads at once.
    //    The row lock serialises them and the reorder rules put them back in order.
    [Fact]
    public async Task Out_of_order_events_for_one_rocket_processed_in_parallel_give_the_in_order_result()
    {
        var channel = PostgresFixture.NewChannel("hot");
        var messages = Sequence(channel, count: 200, seed: 11);
        var processor = db.NewProcessor();

        await Parallel.ForEachAsync(ShuffleWithDuplicates(messages, seed: 11), new ParallelOptions { MaxDegreeOfParallelism = 8 },
            async (m, _) => await processor.ProcessAsync(ToJson(m)));

        Assert.Equal(PostgresFixture.Expected(channel, messages), await db.NewRepository().FindAsync(channel));
        Assert.Equal(200, await db.CountEventsAsync(channel));
    }
}
