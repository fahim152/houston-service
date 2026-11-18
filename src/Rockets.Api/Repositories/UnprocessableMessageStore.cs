using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Rockets.Api.Configuration;
using Rockets.Api.Domain;

namespace Rockets.Api.Repositories;

/// <summary>Bounded in-memory store; when full, the oldest entries are dropped.</summary>
public sealed class UnprocessableMessageStore(IOptions<RocketOptions> options, TimeProvider timeProvider)
    : IUnprocessableMessageStore
{
    private readonly ConcurrentQueue<UnprocessableMessage> _messages = new();
    private readonly int _capacity = options.Value.UnprocessableMessageCapacity;

    public void Add(string payload, string reason)
    {
        _messages.Enqueue(new UnprocessableMessage(timeProvider.GetUtcNow(), reason, payload));
        while (_messages.Count > _capacity && _messages.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<UnprocessableMessage> GetAll() => _messages.ToArray();
}
