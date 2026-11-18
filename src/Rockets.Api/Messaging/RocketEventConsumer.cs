using Microsoft.Extensions.Options;
using Pulsar.Client.Api;
using Pulsar.Client.Common;
using Rockets.Api.Configuration;
using Rockets.Api.Enums;
using Rockets.Api.Services;

namespace Rockets.Api.Messaging;

/// <summary>
/// The background worker. Several consumers share one Key_Shared subscription: each rocket (key) is delivered to one
/// consumer, so different rockets are processed in parallel. A message is acknowledged only after its transaction has
/// committed; otherwise it is negatively acknowledged and Pulsar redelivers it, which the processor recognises as a
/// duplicate if it was in fact already stored.
/// </summary>
public sealed class RocketEventConsumer(
    PulsarClient client,
    RocketEventProcessor processor,
    IOptions<RocketOptions> options,
    ILogger<RocketEventConsumer> logger) : BackgroundService
{
    private const string SubscriptionName = "rockets-worker";

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(1, options.Value.ConsumerCount).Select(_ => ConsumeAsync(stoppingToken)));

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        var consumer = await client.NewConsumer(Schema.STRING())
            .Topic(options.Value.PulsarTopic)
            .SubscriptionName(SubscriptionName)
            .SubscriptionType(SubscriptionType.KeyShared)
            .SubscriptionInitialPosition(SubscriptionInitialPosition.Earliest)
            .NegativeAckRedeliveryDelay(TimeSpan.FromSeconds(1))
            .SubscribeAsync();
        await using var _ = consumer;

        while (!stoppingToken.IsCancellationRequested)
        {
            var message = await consumer.ReceiveAsync(stoppingToken);
            try
            {
                var result = await processor.ProcessAsync(message.GetValue(), stoppingToken);
                if (result == ReceiveResult.BufferFull)
                {
                    // Back-pressure: not stored; Pulsar delivers it again after the redelivery delay.
                    await consumer.NegativeAcknowledge(message.MessageId);
                    continue;
                }

                await consumer.AcknowledgeAsync(message.MessageId);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // e.g. the database is unavailable: nothing was committed, so let Pulsar deliver it again.
                logger.LogError(ex, "Processing failed; the event will be redelivered");
                await consumer.NegativeAcknowledge(message.MessageId);
            }
        }
    }
}
