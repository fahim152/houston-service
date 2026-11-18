namespace Rockets.Api.Events;

public sealed record RocketMissionChanged(string NewMission) : RocketEvent;
