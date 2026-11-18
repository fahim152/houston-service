using Rockets.Api.Repositories;

namespace Rockets.Api.Messaging;

/// <summary>
/// Background service: every second, publishes messages from the outbox to Pulsar (oldest first, 100 per transaction)
/// until the outbox is empty. Runs in every instance; the row locks make each row go to one instance.
/// </summary>
public sealed class OutboxRelay(
    IOutboxRepository outbox,
    IRocketEventPublisher publisher,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int relayed;
                do
                {
                    relayed = await outbox.RelayBatchAsync(
                        (channel, body) => publisher.PublishAsync(channel, body, stoppingToken), BatchSize, stoppingToken);
                    if (relayed > 0)
                    {
                        logger.LogInformation("Relayed {Count} messages from the outbox to Pulsar", relayed);
                    }
                }
                while (relayed > 0);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Pulsar (or Postgres) still unavailable: the rows stay in the outbox; try again shortly.
                logger.LogDebug(ex, "Outbox relay paused: {Reason}", ex.Message);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
