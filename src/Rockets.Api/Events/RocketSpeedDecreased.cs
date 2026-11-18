namespace Rockets.Api.Events;

public sealed record RocketSpeedDecreased(long By) : RocketEvent;
