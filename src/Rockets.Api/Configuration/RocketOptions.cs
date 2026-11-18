using System.ComponentModel.DataAnnotations;

namespace Rockets.Api.Configuration;

public sealed class RocketOptions
{
    public const string SectionName = "Rockets";

    /// <summary>Max out-of-order events waiting per rocket. Beyond it, an early event is not stored and Pulsar redelivers
    /// it later (back-pressure). Without a limit, one missing event could make a rocket's waiting list grow forever.</summary>
    [Range(1, int.MaxValue)]
    public int MaxPendingMessagesPerRocket { get; set; } = 1_000;

    /// <summary>Max unprocessable messages kept in memory; the oldest are dropped first.</summary>
    [Range(1, int.MaxValue)]
    public int UnprocessableMessageCapacity { get; set; } = 10_000;

    /// <summary>Postgres: the event store and each rocket's current state. The default matches compose.yaml's published port.</summary>
    [Required]
    public string PostgresConnectionString { get; set; } = "Host=localhost;Port=55432;Username=rockets;Password=rockets;Database=rockets";

    /// <summary>Pulsar broker the ingest API publishes to and the worker consumes from.</summary>
    [Required]
    public string PulsarServiceUrl { get; set; } = "pulsar://localhost:6650";

    [Required]
    public string PulsarTopic { get; set; } = "persistent://public/default/rocket-events";

    /// <summary>Consumers per service instance (Key_Shared): different rockets in parallel, one rocket on one consumer.</summary>
    [Range(1, 64)]
    public int ConsumerCount { get; set; } = 4;
}
