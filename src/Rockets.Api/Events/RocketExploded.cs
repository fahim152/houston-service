namespace Rockets.Api.Events;

/// <summary>Sent once, if the rocket explodes. Final: later events no longer change the rocket.</summary>
public sealed record RocketExploded(string Reason) : RocketEvent;
