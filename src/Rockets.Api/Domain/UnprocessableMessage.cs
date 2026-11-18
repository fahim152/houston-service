namespace Rockets.Api.Domain;

public sealed record UnprocessableMessage(DateTimeOffset ReceivedAt, string Reason, string Payload);
