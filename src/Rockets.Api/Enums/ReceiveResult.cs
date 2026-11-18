namespace Rockets.Api.Enums;

public enum ReceiveResult
{
    /// <summary>Ingest: the message is valid and was published to Pulsar; the worker applies it later.</summary>
    Accepted,

    /// <summary>The message (and any buffered successors) was applied to the rocket state.</summary>
    Applied,

    /// <summary>The message arrived ahead of a missing predecessor and is held until the gap is filled.</summary>
    Buffered,

    /// <summary>The message was already applied or buffered; it was ignored.</summary>
    Duplicate,

    /// <summary>The reorder buffer is full; the event is redelivered later (back-pressure).</summary>
    BufferFull,

    /// <summary>The message is invalid and can never be applied; it was stored for inspection.</summary>
    Unprocessable,

    /// <summary>Ingest: Pulsar was unavailable, so the message was stored in the outbox; the relay publishes it later.</summary>
    AcceptedToOutbox,

    /// <summary>Ingest: neither Pulsar nor the outbox could store the message; the sender should retry later (503).</summary>
    Unavailable,
}
