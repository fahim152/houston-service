using Microsoft.AspNetCore.Mvc;
using Rockets.Api.Enums;
using Rockets.Api.Requests;
using Rockets.Api.Services;

namespace Rockets.Api.Controllers;

[ApiController]
[Route("rockets")]
public sealed class RocketsController(RocketService service) : ControllerBase
{
    private static readonly Dictionary<string, RocketSortField> SortFields =
        Enum.GetValues<RocketSortField>().ToDictionary(f => f.ToString(), StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, SortOrder> SortOrders =
        Enum.GetValues<SortOrder>().ToDictionary(o => o.ToString(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Lists all launched rockets.</summary>
    /// <param name="sortBy">channel (default), type, speed, mission, status or launchedAt.</param>
    /// <param name="order">asc (default) or desc.</param>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RocketResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] string? sortBy = null, [FromQuery] string? order = null)
    {
        var field = RocketSortField.Channel;
        if (sortBy is not null && !SortFields.TryGetValue(sortBy, out field))
        {
            ModelState.AddModelError(nameof(sortBy), $"Must be one of: {string.Join(", ", SortFields.Keys)}.");
        }

        var direction = SortOrder.Asc;
        if (order is not null && !SortOrders.TryGetValue(order, out direction))
        {
            ModelState.AddModelError(nameof(order), "Must be one of: asc, desc.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var rockets = await service.ListAsync(field, direction, HttpContext.RequestAborted);
        return Ok(rockets.Select(RocketResponse.From).ToList());
    }

    /// <summary>Gets the current state of one rocket.</summary>
    [HttpGet("{channel}")]
    [ProducesResponseType(typeof(RocketResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string channel) =>
        await service.GetAsync(channel, HttpContext.RequestAborted) is { } snapshot
            ? Ok(RocketResponse.From(snapshot))
            : NotFound();
}
