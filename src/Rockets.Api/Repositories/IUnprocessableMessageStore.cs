using Rockets.Api.Domain;

namespace Rockets.Api.Repositories;

/// <summary>
/// Keeps messages that were accepted (to stop redelivery) but can never be processed, so they are not
/// silently lost. Deliberately not called a DLQ: it is in memory and has no replay.
/// </summary>
public interface IUnprocessableMessageStore
{
    void Add(string payload, string reason);

    IReadOnlyList<UnprocessableMessage> GetAll();
}
