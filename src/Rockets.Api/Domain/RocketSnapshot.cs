using Rockets.Api.Enums;

namespace Rockets.Api.Domain;

/// <summary>Immutable, point-in-time view of a rocket that is safe to share across threads.</summary>
public sealed record RocketSnapshot(
    string Channel,
    string? Type,
    long Speed,
    string? Mission,
    RocketStatus Status,
    string? ExplosionReason,
    DateTimeOffset? LaunchedAt,
    DateTimeOffset? LastUpdatedAt,
    long LastMessageNumber,
    int PendingMessages);
