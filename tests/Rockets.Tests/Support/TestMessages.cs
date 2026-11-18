using System.Text.Json;
using Rockets.Api.Events;

namespace Rockets.Tests.Support;

/// <summary>Builds domain messages and their JSON wire format for tests.</summary>
internal static class TestMessages
{
    public const string Channel = "193270a9-c9cf-404a-8f83-838e71d9ae67";

    private static readonly DateTimeOffset BaseTime = new(2022, 2, 2, 19, 39, 5, TimeSpan.FromHours(1));

    public static DateTimeOffset TimeOf(long messageNumber) => BaseTime.AddSeconds(messageNumber);

    public static RocketMessage Launched(
        long number = 1, string type = "Falcon-9", long speed = 500, string mission = "ARTEMIS", string channel = Channel) =>
        new(channel, number, TimeOf(number), new RocketLaunched(type, speed, mission));

    public static RocketMessage Increased(long number, long by, string channel = Channel) =>
        new(channel, number, TimeOf(number), new RocketSpeedIncreased(by));

    public static RocketMessage Decreased(long number, long by, string channel = Channel) =>
        new(channel, number, TimeOf(number), new RocketSpeedDecreased(by));

    public static RocketMessage MissionChanged(long number, string mission, string channel = Channel) =>
        new(channel, number, TimeOf(number), new RocketMissionChanged(mission));

    public static RocketMessage Exploded(long number, string reason = "PRESSURE_VESSEL_FAILURE", string channel = Channel) =>
        new(channel, number, TimeOf(number), new RocketExploded(reason));

    /// <summary>Launch followed by a deterministic mix of speed and mission changes.</summary>
    public static List<RocketMessage> Sequence(string channel, int count, int seed = 42)
    {
        var random = new Random(seed);
        var messages = new List<RocketMessage> { Launched(channel: channel) };
        for (var n = 2; n <= count; n++)
        {
            messages.Add(random.Next(3) switch
            {
                0 => Increased(n, random.Next(0, 5_000), channel),
                1 => Decreased(n, random.Next(0, 5_000), channel),
                _ => MissionChanged(n, $"MISSION_{n}", channel),
            });
        }

        return messages;
    }

    /// <summary>Shuffles the messages and re-adds a share of them to simulate at-least-once delivery.</summary>
    public static List<RocketMessage> ShuffleWithDuplicates(IEnumerable<RocketMessage> messages, int seed)
    {
        var random = new Random(seed);
        var original = messages.ToList();
        var delivered = original.Concat(original.Where(_ => random.NextDouble() < 0.3)).ToArray();
        random.Shuffle(delivered);
        return [.. delivered];
    }

    public static string ToJson(RocketMessage message)
    {
        var (type, payload) = message.Event switch
        {
            RocketLaunched e => ("RocketLaunched", (object)new { type = e.Type, launchSpeed = e.LaunchSpeed, mission = e.Mission }),
            RocketSpeedIncreased e => ("RocketSpeedIncreased", new { by = e.By }),
            RocketSpeedDecreased e => ("RocketSpeedDecreased", new { by = e.By }),
            RocketExploded e => ("RocketExploded", new { reason = e.Reason }),
            RocketMissionChanged e => ("RocketMissionChanged", new { newMission = e.NewMission }),
            _ => throw new ArgumentOutOfRangeException(nameof(message)),
        };

        return JsonSerializer.Serialize(new
        {
            metadata = new
            {
                channel = message.Channel,
                messageNumber = message.MessageNumber,
                messageTime = message.MessageTime,
                messageType = type,
            },
            message = payload,
        });
    }
}
