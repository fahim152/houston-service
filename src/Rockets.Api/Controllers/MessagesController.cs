using Microsoft.AspNetCore.Mvc;
using Rockets.Api.Domain;
using Rockets.Api.Enums;
using Rockets.Api.Requests;
using Rockets.Api.Services;

namespace Rockets.Api.Controllers;

/// <summary>
/// Receives messages from the rockets test program. The sender redelivers on any non-2xx response,
/// so the status code decides whether a message is retried.
/// </summary>
[ApiController]
public sealed class MessagesController(RocketService service) : ControllerBase
{
    public const string ResultHeader = "X-Ingest-Result";

    [HttpPost("messages")]
    [Consumes(typeof(RocketMessageRequest), "application/json")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Receive()
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(HttpContext.RequestAborted);

        var result = await service.ReceiveAsync(body, HttpContext.RequestAborted);

        if (result == ReceiveResult.Unavailable)
        {
            // Neither Pulsar nor the outbox stored it, so it must be resent: 503 + Retry-After ("temporarily unavailable").
            Response.Headers.RetryAfter = "2";
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Message storage unavailable",
                detail: "The message was not stored. Retry later.");
        }

        // Accepted (in Pulsar), AcceptedToOutbox (in Postgres, published when Pulsar is back) and Unprocessable all get
        // 202, so the sender stops redelivering.
        // errors and retries, while an empty 202 caused none. The outcome goes in a header instead.
        Response.Headers[ResultHeader] = result.ToString();
        return StatusCode(StatusCodes.Status202Accepted);
    }

    [HttpGet("unprocessable-messages")]
    public IReadOnlyList<UnprocessableMessage> GetUnprocessable() => service.GetUnprocessable();
}
