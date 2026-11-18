using Rockets.Api.Domain;
using Rockets.Api.Enums;

namespace Rockets.Api.Requests;

/// <summary>What the API returns for a rocket. Kept separate from the domain snapshot so the contract can evolve on its own.</summary>
public sealed record RocketResponse(
    string Channel,
    string? Type,
    long Speed,
    string? Mission,
    RocketStatus Status,
    string? ExplosionReason,
    DateTimeOffset? LaunchedAt,
    DateTimeOffset? LastUpdatedAt,
    long LastMessageNumber,
    int PendingMessages)
{
    public static RocketResponse From(RocketSnapshot s) => new(
        s.Channel, s.Type, s.Speed, s.Mission, s.Status, s.ExplosionReason,
        s.LaunchedAt, s.LastUpdatedAt, s.LastMessageNumber, s.PendingMessages);
}
