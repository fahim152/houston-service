using Rockets.Api.Domain;
using Rockets.Api.Enums;
using Rockets.Api.Repositories;
using Rockets.Tests.Support;
using static Rockets.Tests.Support.TestMessages;

namespace Rockets.Tests.Processing;

/// <summary>The worker's rules, one concern per test, against a real Postgres.</summary>
[Collection(PostgresCollection.Name)]
public class EventProcessingTests(PostgresFixture db)
{
    // Out of order: #3 and #4 arrive before #2. They are stored and wait; when #2 arrives, 2, 3, 4 apply in order.
    [Fact]
    public async Task Early_events_wait_until_the_gap_is_filled()
    {
        var channel = PostgresFixture.NewChannel("gap");
        var processor = db.NewProcessor();
        await processor.ProcessAsync(ToJson(Launched(channel: channel, speed: 500)));

        Assert.Equal(ReceiveResult.Buffered, await processor.ProcessAsync(ToJson(MissionChanged(3, "THIRD", channel))));
        Assert.Equal(ReceiveResult.Buffered, await processor.ProcessAsync(ToJson(Increased(4, 1000, channel))));
        var waiting = await db.NewRepository().FindAsync(channel);
        Assert.Equal((1L, 2), (waiting!.LastMessageNumber, waiting.PendingMessages));     // the gap is visible

        Assert.Equal(ReceiveResult.Applied, await processor.ProcessAsync(ToJson(MissionChanged(2, "SECOND", channel))));
        var done = await db.NewRepository().FindAsync(channel);
        Assert.Equal((4L, 0, "THIRD", 1500L), (done!.LastMessageNumber, done.PendingMessages, done.Mission, done.Speed));
    }

    // Out of order, any order: mission changes and the explosion depend on order, so 20 random arrival orders (with
    // duplicates) must all give the same result as in-order delivery, including "nothing changes after the explosion".
    [Fact]
    public async Task Any_arrival_order_with_duplicates_gives_the_in_order_result()
    {
        var processor = db.NewProcessor();
        for (var seed = 1; seed <= 20; seed++)
        {
            var channel = PostgresFixture.NewChannel($"order{seed}");
            var messages = Sequence(channel, count: 40, seed: seed);
            messages[29] = Exploded(30, channel: channel);                   // #31…#40 arrive after the explosion

            foreach (var m in ShuffleWithDuplicates(messages, seed))
            {
                await processor.ProcessAsync(ToJson(m));
            }

            var rocket = await db.NewRepository().FindAsync(channel);
            Assert.Equal(PostgresFixture.Expected(channel, messages), rocket);
            Assert.Equal((RocketStatus.Exploded, 40L), (rocket!.Status, rocket.LastMessageNumber));
            // Independent of the rule under test: the speed is what it was at the explosion (#1…#30 only).
            Assert.Equal(PostgresFixture.Expected(channel, messages.Take(30)).Speed, rocket.Speed);
        }
    }

    // Waiting events live in Postgres, not in memory: #3 and #4 wait, the service restarts (a new instance with its
    // own connections), and when #2 arrives the new instance applies 2, 3 and 4.
    [Fact]
    public async Task Waiting_events_survive_a_restart()
    {
        var channel = PostgresFixture.NewChannel("restart");
        var beforeRestart = db.NewProcessor();
        await beforeRestart.ProcessAsync(ToJson(Launched(channel: channel, speed: 500)));
        await beforeRestart.ProcessAsync(ToJson(Increased(3, 1000, channel)));
        await beforeRestart.ProcessAsync(ToJson(Increased(4, 1000, channel)));

        var afterRestart = db.NewProcessor(separateInstance: true);
        Assert.Equal(ReceiveResult.Applied, await afterRestart.ProcessAsync(ToJson(Increased(2, 1000, channel))));

        var rocket = await db.NewRepository().FindAsync(channel);
        Assert.Equal((4L, 0, 3500L), (rocket!.LastMessageNumber, rocket.PendingMessages, rocket.Speed));
    }

    // The launch starts the sequence at its own number N; anything numbered before it is dropped.
    [Fact]
    public async Task The_launch_starts_the_sequence_and_earlier_numbers_are_dropped()
    {
        var channel = PostgresFixture.NewChannel("late-start");
        var processor = db.NewProcessor();

        Assert.Equal(ReceiveResult.Buffered, await processor.ProcessAsync(ToJson(Increased(8, 1000, channel))));
        Assert.Equal(ReceiveResult.Buffered, await processor.ProcessAsync(ToJson(Increased(6, 5000, channel)))); // before the launch
        Assert.Equal(ReceiveResult.Applied, await processor.ProcessAsync(ToJson(Launched(7, channel: channel, speed: 500))));

        var rocket = await db.NewRepository().FindAsync(channel);
        Assert.Equal((8, 1500, 0), (rocket!.LastMessageNumber, rocket.Speed, rocket.PendingMessages)); // #8 applied, #6 not
    }

    // Back-pressure: with the buffer full, an early event is not stored (the consumer nacks it, Pulsar redelivers it).
    // The missing event is always accepted, so the buffer drains and the redelivery then succeeds.
    [Fact]
    public async Task A_full_buffer_rejects_early_events_and_the_redelivery_succeeds()
    {
        var channel = PostgresFixture.NewChannel("full");
        var processor = db.NewProcessor(maxPendingMessages: 2);
        await processor.ProcessAsync(ToJson(Launched(channel: channel, speed: 0)));
        await processor.ProcessAsync(ToJson(Increased(3, 1, channel)));
        await processor.ProcessAsync(ToJson(Increased(4, 1, channel)));

        Assert.Equal(ReceiveResult.BufferFull, await processor.ProcessAsync(ToJson(Increased(5, 1, channel))));
        Assert.Equal(3, await db.CountEventsAsync(channel));                                            // #5 not stored

        Assert.Equal(ReceiveResult.Applied, await processor.ProcessAsync(ToJson(Increased(2, 1, channel))));
        Assert.Equal(ReceiveResult.Applied, await processor.ProcessAsync(ToJson(Increased(5, 1, channel)))); // redelivery
        var rocket = await db.NewRepository().FindAsync(channel);
        Assert.Equal((5L, 4L), (rocket!.LastMessageNumber, rocket.Speed));
    }

    // Crash after commit, before the Pulsar ack: Pulsar redelivers the event, possibly to another (restarted)
    // instance. It is recognised as a duplicate, so the speed is counted once.
    [Fact]
    public async Task An_event_redelivered_after_a_crash_is_a_duplicate()
    {
        var channel = PostgresFixture.NewChannel("redelivery");
        var before = db.NewProcessor();
        await before.ProcessAsync(ToJson(Launched(channel: channel, speed: 500)));
        await before.ProcessAsync(ToJson(Increased(2, 3000, channel)));      // committed… then the "crash", no ack

        var afterRestart = db.NewProcessor(separateInstance: true);          // new instance, same database
        Assert.Equal(ReceiveResult.Duplicate, await afterRestart.ProcessAsync(ToJson(Increased(2, 3000, channel))));
        Assert.Equal(3500, (await db.NewRepository().FindAsync(channel))!.Speed);
    }

    // Crash in the middle of the transaction (after the writes, before the commit): everything rolls back, nothing is
    // half-saved, and the redelivered event is processed normally.
    [Fact]
    public async Task A_crash_before_commit_rolls_back_and_the_redelivery_is_applied()
    {
        var channel = PostgresFixture.NewChannel("rollback");
        await db.NewProcessor().ProcessAsync(ToJson(Launched(channel: channel, speed: 500)));

        var crashing = db.NewProcessor(repository: new CrashBeforeCommit(db.NewRepository()));
        await Assert.ThrowsAsync<IOException>(() => crashing.ProcessAsync(ToJson(Increased(2, 3000, channel))));
        Assert.Equal(1, await db.CountEventsAsync(channel));                                     // event not stored
        Assert.Equal(500, (await db.NewRepository().FindAsync(channel))!.Speed);                  // state unchanged

        Assert.Equal(ReceiveResult.Applied, await db.NewProcessor().ProcessAsync(ToJson(Increased(2, 3000, channel))));
        Assert.Equal(3500, (await db.NewRepository().FindAsync(channel))!.Speed);
    }

    /// <summary>A real repository whose transactions fail at commit, like a process killed just before it.</summary>
    private sealed class CrashBeforeCommit(IRocketRepository inner) : IRocketRepository
    {
        public Task InitializeAsync(CancellationToken ct = default) => inner.InitializeAsync(ct);

        public async Task<IRocketTransaction> LockAsync(string channel, CancellationToken ct = default) =>
            new Transaction(await inner.LockAsync(channel, ct));

        public Task<RocketSnapshot?> FindAsync(string channel, CancellationToken ct = default) => inner.FindAsync(channel, ct);

        public Task<IReadOnlyList<RocketSnapshot>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);

        private sealed class Transaction(IRocketTransaction inner) : IRocketTransaction
        {
            public RocketSnapshot State => inner.State;

            public Task<IReadOnlyList<string>> GetWaitingEventsAsync(CancellationToken ct = default) => inner.GetWaitingEventsAsync(ct);

            public Task SaveAsync(string channel, long number, string body, RocketSnapshot state, CancellationToken ct = default) =>
                inner.SaveAsync(channel, number, body, state, ct);

            public Task CommitAsync(CancellationToken ct = default) => throw new IOException("crash before commit");

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
