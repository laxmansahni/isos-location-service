using ISOS.LocationService.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ISOS.LocationService.Ingestion;

[ApiController]
[Route("v1/locationtracking")]
public sealed class LocationTrackingController(
    IngestionService ingestion,
    ICallerContext callerContext,
    IOptions<IngestionOptions> options) : ControllerBase
{
    /// <summary>Submits 1 to 100 location points from the Assistance App.</summary>
    /// <remarks>
    /// Returns 200 with a per-point result. The device deletes points reported as Stored, Duplicate or
    /// Rejected (LLD 4.1). A batch that is empty, too large or has no device id is rejected as a whole with 400.
    /// </remarks>
    [HttpPost("points")]
    [ProducesResponseType<SubmitPointsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SubmitPoints([FromBody] SubmitPointsRequest request, CancellationToken cancellationToken)
    {
        var caller = callerContext.Current;
        if (caller is null)
        {
            return Unauthorized();
        }

        var maxBatch = options.Value.MaxBatchSize;
        if (request.Points is not { Count: > 0 } points)
        {
            return BadRequestProblem("Request must contain at least one point.");
        }

        if (points.Count > maxBatch)
        {
            return BadRequestProblem($"A request may contain at most {maxBatch} points.");
        }

        if (request.Device is not { Id: { Length: > 0 } } device)
        {
            return BadRequestProblem("device.id is required.");
        }

        var response = await ingestion.SubmitAsync(caller, device, points, cancellationToken);
        return Ok(response);
    }

    private BadRequestObjectResult BadRequestProblem(string detail) =>
        BadRequest(new ProblemDetails { Title = "Invalid request", Detail = detail, Status = StatusCodes.Status400BadRequest });
}
