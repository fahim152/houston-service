using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Rockets.Api.Events;
using Rockets.Api.Requests;

namespace Rockets.Api.Services;

/// <summary>Turns the raw JSON body into a domain <see cref="RocketMessage"/>, or explains why it can't.</summary>
public static class RocketMessageMapper
{
    // Strict on purpose: camelCase names as sent, and numbers must be JSON numbers (not "1").
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // messageType → how to build that event from the payload (null when a field is missing or invalid).
    // Exact, case-sensitive names: the event class names in Events/ are the wire format.
    private static readonly Dictionary<string, Func<MessagePayload, RocketEvent?>> EventFactories = new()
    {
        [nameof(RocketLaunched)] = p => HasText(p.Type) && HasText(p.Mission) && p.LaunchSpeed >= 0
            ? new RocketLaunched(p.Type, p.LaunchSpeed.Value, p.Mission) : null,
        [nameof(RocketSpeedIncreased)] = p => p.By >= 0 ? new RocketSpeedIncreased(p.By.Value) : null,
        [nameof(RocketSpeedDecreased)] = p => p.By >= 0 ? new RocketSpeedDecreased(p.By.Value) : null,
        [nameof(RocketMissionChanged)] = p => HasText(p.NewMission) ? new RocketMissionChanged(p.NewMission) : null,
        [nameof(RocketExploded)] = p => HasText(p.Reason) ? new RocketExploded(p.Reason) : null,
    };

    public static bool TryMap(
        string body,
        [NotNullWhen(true)] out RocketMessage? message,
        [NotNullWhen(false)] out string? error)
    {
        message = null;
        error = null;

        // 1. Is it valid JSON in the expected shape?
        RocketMessageRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<RocketMessageRequest>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return false;
        }

        // 2. Are the metadata fields present and valid?
        var metadata = request?.Metadata;
        var payload = request?.Message;
        if (metadata is null || payload is null)
        {
            error = "'metadata' and 'message' are required.";
        }
        else if (string.IsNullOrWhiteSpace(metadata.Channel))
        {
            error = "'channel' is required.";
        }
        else if (metadata.MessageNumber < 1)
        {
            error = "'messageNumber' must be 1 or higher.";
        }
        else if (metadata.MessageTime is null)
        {
            error = "'messageTime' is required.";
        }
        else if (metadata.MessageType is null || !EventFactories.ContainsKey(metadata.MessageType))
        {
            error = $"Unknown messageType '{metadata.MessageType}'.";
        }

        if (error is not null)
        {
            return false;
        }

        // 3. Does the payload have the fields this message type needs?
        var messageType = metadata!.MessageType!;
        var rocketEvent = EventFactories[messageType](payload!);
        if (rocketEvent is null)
        {
            error = $"Invalid or missing payload fields for {messageType}.";
            return false;
        }

        message = new RocketMessage(metadata.Channel!, metadata.MessageNumber, metadata.MessageTime!.Value, rocketEvent);
        return true;
    }

    private static bool HasText([NotNullWhen(true)] string? value) => !string.IsNullOrWhiteSpace(value);
}
