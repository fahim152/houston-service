namespace Rockets.Api.Events;

/// <summary>An event with its metadata: which rocket (channel), its position in that rocket's sequence, and when it was sent.</summary>
public sealed record RocketMessage(string Channel, long MessageNumber, DateTimeOffset MessageTime, RocketEvent Event);
