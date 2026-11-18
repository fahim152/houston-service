using Microsoft.Extensions.Options;
using Pulsar.Client.Api;
using Rockets.Api.Configuration;

namespace Rockets.Api.Messaging;

/// <summary>
/// Publishes each accepted message to Pulsar with the channel as its key, so all of a rocket's events go to the same
/// consumer (Key_Shared subscription). The raw JSON is published unchanged.
/// </summary>
public sealed class PulsarRocketEventPublisher(PulsarClient client, IOptions<RocketOptions> options)
    : IRocketEventPublisher, IAsyncDisposable
{
    // Fail fast instead of Pulsar's default 30 s, so the sender gets its 503 quickly and retries. If the message still
    // reaches Pulsar after the timeout, the sender's retry is a second copy, which the worker treats as a duplicate.
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(3);

    private readonly SemaphoreSlim _createGate = new(1, 1);
    private IProducer<string>? _producer;

    public async Task PublishAsync(string channel, string body, CancellationToken cancellationToken = default)
    {
        var producer = await GetProducerAsync(cancellationToken);
        await producer.SendAsync(producer.NewMessage(body, channel))
            .WaitAsync(SendTimeout, cancellationToken);
    }

    // Created on first use, and created again on a later call if creating it failed (e.g. Pulsar was still starting).
    private async Task<IProducer<string>> GetProducerAsync(CancellationToken cancellationToken)
    {
        if (_producer is { } existing)
        {
            return existing;
        }

        await _createGate.WaitAsync(cancellationToken);
        try
        {
            // Batching off: a batch with several keys would be delivered to one consumer, breaking "one rocket, one consumer".
            return _producer ??= await client.NewProducer(Schema.STRING())
                .Topic(options.Value.PulsarTopic)
                .EnableBatching(false)
                .SendTimeout(SendTimeout)
                .CreateAsync()
                .WaitAsync(SendTimeout, cancellationToken);
        }
        finally
        {
            _createGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_producer is not null)
        {
            await _producer.DisposeAsync();
        }
    }
}
