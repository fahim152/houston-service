using Microsoft.Extensions.Options;
using Rockets.Api.Configuration;
using Rockets.Api.Domain;
using Rockets.Api.Enums;
using Rockets.Api.Repositories;

namespace Rockets.Api.Services;

/// <summary>
/// Applies one event from Pulsar, in one Postgres transaction:
/// 1. lock the rocket's row (no other thread or instance can process this rocket meanwhile);
/// 2. rebuild the rocket from its saved state plus its waiting events;
/// 3. let <see cref="Rocket"/> decide: applied, buffered (early), duplicate, or buffer full;
/// 4. store the event and the new state, and commit.
/// A crash at any point rolls back; Pulsar then redelivers the event, and it is processed as if for the first time.
/// </summary>
public sealed class RocketEventProcessor(
    IRocketRepository repository,
    IOptions<RocketOptions> options,
    RocketMetrics metrics,
    ILogger<RocketEventProcessor> logger)
{
    private readonly int _maxPendingMessages = options.Value.MaxPendingMessagesPerRocket;

    public async Task<ReceiveResult> ProcessAsync(string body, CancellationToken cancellationToken = default)
    {
        if (!RocketMessageMapper.TryMap(body, out var message, out var error))
        {
            // The ingest API only publishes valid messages, so this means a foreign or corrupted message: drop it.
            logger.LogWarning("Dropped an invalid event from Pulsar: {Reason}", error);
            return ReceiveResult.Unprocessable;
        }

        await using var transaction = await repository.LockAsync(message.Channel, cancellationToken);

        var rocket = Rocket.FromSnapshot(transaction.State, _maxPendingMessages);
        foreach (var waiting in await transaction.GetWaitingEventsAsync(cancellationToken))
        {
            if (RocketMessageMapper.TryMap(waiting, out var waitingMessage, out _))
            {
                rocket.Receive(waitingMessage);   // back into the reorder buffer, as before the restart
            }
        }

        var result = rocket.Receive(message);
        if (result is ReceiveResult.Applied or ReceiveResult.Buffered)
        {
            await transaction.SaveAsync(message.Channel, message.MessageNumber, body, rocket.Snapshot(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        // Duplicate: nothing to store. BufferFull: not stored, redelivered later. Both just end the transaction.

        logger.LogDebug(
            "Message {Channel}#{MessageNumber} ({MessageType}): {Result}",
            message.Channel, message.MessageNumber, message.Event.GetType().Name, result);
        metrics.MessageReceived(result);
        return result;
    }
}
