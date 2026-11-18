using System.Diagnostics.Metrics;
using Rockets.Api.Enums;

namespace Rockets.Api.Services;

/// <summary>
/// Counts every received message by its result (Applied, Buffered, Duplicate, Unprocessable, BufferFull).
/// Exported with OpenTelemetry when an OTLP endpoint is configured; when nothing listens, a counter costs almost nothing.
/// </summary>
public sealed class RocketMetrics
{
    public const string MeterName = "Rockets.Api";

    private readonly Counter<long> _messages;

    public RocketMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _messages = meter.CreateCounter<long>(
            "rockets.messages", unit: "{message}", description: "Messages received, by ingest result.");
    }

    public void MessageReceived(ReceiveResult result) =>
        _messages.Add(1, new KeyValuePair<string, object?>("result", result.ToString()));
}
