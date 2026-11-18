namespace Rockets.Api.Events;

/// <summary>Sent once, when the rocket launches. Its message number starts the rocket's sequence.</summary>
public sealed record RocketLaunched(string Type, long LaunchSpeed, string Mission) : RocketEvent;
