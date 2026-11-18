using Rockets.Api.Domain;

namespace Rockets.Api.Repositories;

/// <summary>Postgres storage: the event store (every accepted event) and each rocket's current state.</summary>
public interface IRocketRepository
{
    /// <summary>Creates the tables if they don't exist yet.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a transaction and locks the rocket's row (<c>SELECT … FOR UPDATE</c>), creating it if needed. Until the
    /// transaction ends, no other thread or service instance can process this rocket.
    /// </summary>
    Task<IRocketTransaction> LockAsync(string channel, CancellationToken cancellationToken = default);

    Task<RocketSnapshot?> FindAsync(string channel, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RocketSnapshot>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>One rocket, locked for the duration of the transaction. Disposing without <see cref="CommitAsync"/> rolls back.</summary>
public interface IRocketTransaction : IAsyncDisposable
{
    /// <summary>The rocket's saved state when it was locked.</summary>
    RocketSnapshot State { get; }

    /// <summary>Stored events numbered after <c>State.LastMessageNumber</c>: the ones still waiting, in order.</summary>
    Task<IReadOnlyList<string>> GetWaitingEventsAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores the new event and the rocket's new state.</summary>
    Task SaveAsync(string channel, long messageNumber, string body, RocketSnapshot newState, CancellationToken cancellationToken = default);

    Task CommitAsync(CancellationToken cancellationToken = default);
}
