using Rockets.Api.Enums;
using Rockets.Api.Events;

namespace Rockets.Api.Domain;

/// <summary>
/// The state of a single rocket, built by applying its messages strictly in message-number order.
/// Out-of-order messages are held in a reorder buffer; duplicates are ignored.
/// Not thread-safe: a Rocket is rebuilt for each event and used by one thread, inside the transaction that holds the
/// rocket's Postgres row lock (see RocketEventProcessor).
/// </summary>
public sealed class Rocket
{
    private readonly SortedDictionary<long, RocketMessage> _pending = new();
    private readonly int _maxPendingMessages;

    private long _lastAppliedNumber;
    private string? _type;
    private long _speed;
    private string? _mission;
    private RocketStatus _status = RocketStatus.AwaitingLaunch;
    private string? _explosionReason;
    private DateTimeOffset? _launchedAt;
    private DateTimeOffset? _lastUpdatedAt;

    public Rocket(string channel, int maxPendingMessages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPendingMessages);

        Channel = channel;
        _maxPendingMessages = maxPendingMessages;
    }

    public string Channel { get; }

    /// <summary>Recreates a rocket from its saved state.</summary>
    public static Rocket FromSnapshot(RocketSnapshot saved, int maxPendingMessages) =>
        new(saved.Channel, maxPendingMessages)
        {
            _type = saved.Type,
            _speed = saved.Speed,
            _mission = saved.Mission,
            _status = saved.Status,
            _explosionReason = saved.ExplosionReason,
            _launchedAt = saved.LaunchedAt,
            _lastUpdatedAt = saved.LastUpdatedAt,
            _lastAppliedNumber = saved.LastMessageNumber,
        };

    /// <summary>
    /// Applies the message if it is next in the sequence (then any waiting ones that follow), holds it if it is early,
    /// and ignores it if it was already seen. Saving the result is up to the caller (see RocketEventProcessor).
    /// </summary>
    public ReceiveResult Receive(RocketMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Channel != Channel)
        {
            throw new ArgumentException(
                $"Message for channel '{message.Channel}' cannot be applied to rocket '{Channel}'.", nameof(message));
        }

        if (message.MessageNumber <= _lastAppliedNumber || _pending.ContainsKey(message.MessageNumber))
        {
            return ReceiveResult.Duplicate;
        }

        // Until the rocket has launched, only its RocketLaunched can be applied, and its number N starts the sequence
        // (N doesn't have to be 1). After that, messages are applied strictly in order: N+1, N+2, …
        var canApply = _status == RocketStatus.AwaitingLaunch
            ? message.Event is RocketLaunched
            : message.MessageNumber == _lastAppliedNumber + 1;

        if (!canApply)
        {
            if (_pending.Count >= _maxPendingMessages)
            {
                return ReceiveResult.BufferFull;
            }

            _pending.Add(message.MessageNumber, message);
            return ReceiveResult.Buffered;
        }

        Apply(message);
        if (message.Event is RocketLaunched)
        {
            // Waiting messages numbered before the launch can never be applied, so they are dropped.
            foreach (var number in _pending.Keys.TakeWhile(n => n < _lastAppliedNumber).ToList())
            {
                _pending.Remove(number);
            }
        }

        while (_pending.Remove(_lastAppliedNumber + 1, out var next))
        {
            Apply(next);
        }

        return ReceiveResult.Applied;
    }

    public RocketSnapshot Snapshot() =>
        new(
            Channel,
            _type,
            _speed,
            _mission,
            _status,
            _explosionReason,
            _launchedAt,
            _lastUpdatedAt,
            _lastAppliedNumber,
            _pending.Count);

    private void Apply(RocketMessage message)
    {
        _lastAppliedNumber = message.MessageNumber;

        // Exploded is terminal: later messages advance the sequence but no longer change state.
        if (_status == RocketStatus.Exploded)
        {
            return;
        }

        switch (message.Event)
        {
            case RocketLaunched launched:
                _type = launched.Type;
                _speed = launched.LaunchSpeed;
                _mission = launched.Mission;
                _status = RocketStatus.Launched;
                _launchedAt = message.MessageTime;
                break;
            case RocketSpeedIncreased increased:
                _speed += increased.By;
                break;
            case RocketSpeedDecreased decreased:
                _speed -= decreased.By;
                break;
            case RocketMissionChanged missionChanged:
                _mission = missionChanged.NewMission;
                break;
            case RocketExploded exploded:
                _status = RocketStatus.Exploded;
                _explosionReason = exploded.Reason;
                break;
            default:
                throw new InvalidOperationException($"Unsupported rocket event '{message.Event.GetType().Name}'.");
        }

        _lastUpdatedAt = message.MessageTime;
    }
}
