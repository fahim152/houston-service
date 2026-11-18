namespace Rockets.Api.Repositories;

/// <summary>
/// Store-and-forward for when Pulsar is unavailable: ingest stores the message here, and the outbox relay publishes it
/// to Pulsar once Pulsar is back.
/// </summary>
public interface IOutboxRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task AddAsync(string channel, long messageNumber, string body, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> of the oldest rows (rows locked by another instance are skipped),
    /// calls <paramref name="publish"/> for each, oldest first, then deletes them, all in one transaction.
    /// If publishing throws, nothing is deleted and the rows are retried later. Returns how many rows were relayed.
    /// </summary>
    Task<int> RelayBatchAsync(Func<string, string, Task> publish, int batchSize, CancellationToken cancellationToken = default);
}
