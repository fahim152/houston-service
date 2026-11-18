using System.ComponentModel;

namespace Rockets.Api.Requests;

/// <summary>Body of <c>POST /messages</c>, exactly as the rockets test program sends it.</summary>
public sealed class RocketMessageRequest
{
    public MessageMetadata? Metadata { get; set; }

    public MessagePayload? Message { get; set; }
}

public sealed class MessageMetadata
{
    [Description("Unique per rocket; used as the rocket ID.")]
    public string? Channel { get; set; }

    [Description("Order of the message within the channel, starting at 1.")]
    public long MessageNumber { get; set; }

    public DateTimeOffset? MessageTime { get; set; }

    [Description("RocketLaunched, RocketSpeedIncreased, RocketSpeedDecreased, RocketExploded or RocketMissionChanged.")]
    public string? MessageType { get; set; }
}

/// <summary>
/// One class for every payload shape; only the fields of the given messageType are used:
/// RocketLaunched {type, launchSpeed, mission} · RocketSpeedIncreased/Decreased {by} ·
/// RocketExploded {reason} · RocketMissionChanged {newMission}.
/// </summary>
public sealed class MessagePayload
{
    public string? Type { get; set; }

    public long? LaunchSpeed { get; set; }

    public string? Mission { get; set; }

    public long? By { get; set; }

    public string? Reason { get; set; }

    public string? NewMission { get; set; }
}
