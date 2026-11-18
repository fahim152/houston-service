namespace Rockets.Api.Messaging;

public interface IRocketEventPublisher
{
    /// <summary>Publishes the raw message, keyed by channel. Returns once the broker has stored it.</summary>
    Task PublishAsync(string channel, string body, CancellationToken cancellationToken = default);
}
