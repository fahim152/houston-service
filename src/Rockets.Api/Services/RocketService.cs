using Rockets.Api.Domain;
using Rockets.Api.Enums;
using Rockets.Api.Events;
using Rockets.Api.Messaging;
using Rockets.Api.Repositories;

namespace Rockets.Api.Services;

/// <summary>
/// Ingest and queries. Ingest only validates and publishes to Pulsar; the worker (RocketEventProcessor) applies the
/// events. Queries read each rocket's current state from Postgres.
/// </summary>
public sealed class RocketService(
    IRocketRepository repository,
    IRocketEventPublisher publisher,
    IOutboxRepository outbox,
    IUnprocessableMessageStore unprocessableMessages,
    RocketMetrics metrics,
    ILogger<RocketService> logger)
{
    // Circuit breaker: after Pulsar fails, go straight to the outbox for a while instead of waiting for a timeout
    // on every message. Shared by all requests (RocketService is a singleton).
    private static readonly TimeSpan PulsarRetryAfter = TimeSpan.FromSeconds(5);
    private long _skipPulsarUntilTicks;

    public async Task<ReceiveResult> ReceiveAsync(string body, CancellationToken cancellationToken = default)
    {
        if (!RocketMessageMapper.TryMap(body, out var message, out var error))
        {
            // Retrying can never fix it, so it is kept for inspection instead of being rejected (see MessagesController).
            unprocessableMessages.Add(body, error);
            // The raw JSON goes with this warning as a "Body" field (visible in the Aspire Dashboard's log details).
            using (logger.BeginScope(new Dictionary<string, object> { ["Body"] = body }))
            {
                logger.LogWarning("Unprocessable message: {Reason}", error);
            }
            metrics.MessageReceived(ReceiveResult.Unprocessable);
            return ReceiveResult.Unprocessable;
        }

        var result = await StoreAsync(message.Channel, message.MessageNumber, body, cancellationToken);
        metrics.MessageReceived(result);
        return result;
    }

    // Pulsar first; if it's unavailable, the outbox (Postgres); if that fails too, Unavailable → 503 → the sender retries.
    private async Task<ReceiveResult> StoreAsync(string channel, long messageNumber, string body, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow.Ticks >= Interlocked.Read(ref _skipPulsarUntilTicks))
        {
            try
            {
                await publisher.PublishAsync(channel, body, cancellationToken);   // returns once Pulsar has stored it
                return ReceiveResult.Accepted;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _skipPulsarUntilTicks, (DateTime.UtcNow + PulsarRetryAfter).Ticks);
                logger.LogWarning(ex, "Pulsar unavailable; storing messages in the outbox for the next {Seconds} s",
                    PulsarRetryAfter.TotalSeconds);
            }
        }

        try
        {
            await outbox.AddAsync(channel, messageNumber, body, cancellationToken);
            return ReceiveResult.AcceptedToOutbox;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Pulsar and the outbox are unavailable; message {Channel}#{MessageNumber} not accepted",
                channel, messageNumber);
            return ReceiveResult.Unavailable;
        }
    }

    /// <summary>Returns the rocket, or null if it is unknown or has not launched yet.</summary>
    public async Task<RocketSnapshot?> GetAsync(string channel, CancellationToken cancellationToken = default)
    {
        var snapshot = await repository.FindAsync(channel, cancellationToken);
        return snapshot is { Status: not RocketStatus.AwaitingLaunch } ? snapshot : null;
    }

    public async Task<IReadOnlyList<RocketSnapshot>> ListAsync(
        RocketSortField sortBy, SortOrder order, CancellationToken cancellationToken = default)
    {
        var launched = (await repository.ListAsync(cancellationToken))
            .Where(snapshot => snapshot.Status != RocketStatus.AwaitingLaunch);

        var descending = order == SortOrder.Desc;
        var sorted = sortBy switch
        {
            RocketSortField.Channel => OrderBy(launched, s => s.Channel, descending),
            RocketSortField.Type => OrderBy(launched, s => s.Type, descending),
            RocketSortField.Speed => OrderBy(launched, s => s.Speed, descending),
            RocketSortField.Mission => OrderBy(launched, s => s.Mission, descending),
            RocketSortField.Status => OrderBy(launched, s => s.Status, descending),
            RocketSortField.LaunchedAt => OrderBy(launched, s => s.LaunchedAt, descending),
            _ => throw new ArgumentOutOfRangeException(nameof(sortBy), sortBy, null),
        };

        // Channel is unique, so it makes the order deterministic when the sort key ties.
        return sorted.ThenBy(s => s.Channel, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<UnprocessableMessage> GetUnprocessable() => unprocessableMessages.GetAll();

    private static IOrderedEnumerable<RocketSnapshot> OrderBy<TKey>(
        IEnumerable<RocketSnapshot> source, Func<RocketSnapshot, TKey> key, bool descending) =>
        descending ? source.OrderByDescending(key) : source.OrderBy(key);
}
