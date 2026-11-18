namespace Rockets.Api.Events;

/// <summary>Something that happened to a rocket. The class name is the wire format of <c>metadata.messageType</c>.</summary>
public abstract record RocketEvent;
