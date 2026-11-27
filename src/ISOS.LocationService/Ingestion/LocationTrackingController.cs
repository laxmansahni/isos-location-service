using ISOS.LocationService.Identity;
using ISOS.LocationService.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ISOS.LocationService.Ingestion;

[ApiController]
[Route("v1/locationtracking")]
public sealed class LocationTrackingController(
    IngestionService ingestion,
    PolicyService policyService,
    PolicyEvaluator policyEvaluator,
    ICallerContext callerContext,
    IOptions<IngestionOptions> options) : ControllerBase
{
    /// <summary>Submits 1 to 100 location points from the Assistance App.</summary>
    /// <remarks>
    /// Returns 200 with a per-point result. The device deletes points reported as Stored, Duplicate or
    /// Rejected (LLD 4.1). A batch that is empty, too large or has no device id is rejected as a whole with 400.
    /// Live-sharing points (<c>shared: true</c>) are checked against the person's policy: when the client has not
    /// enabled sharing, or the person has not consented, a batch of only live points gets 403 SHARING_NOT_ALLOWED or
    /// CONSENT_REQUIRED with the current policy. In a mixed batch those points are rejected one by one instead.
    /// A point captured before consent or the visibility fence, or outside the chosen sharing window, is rejected.
    /// Not-shared points (AEC and Location Based Alerts only) are never checked against sharing consent.
    /// </remarks>
    [HttpPost("points")]
    [ProducesResponseType<SubmitPointsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
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

        PolicySnapshot? policy = null;
        if (points.Any(p => p.Shared == true))
        {
            if (caller.ClientId is null)
            {
                return BadRequestProblem("A client is required to send live-sharing points. In Development send the clientId header.");
            }

            policy = await policyService.LoadAsync(caller.PartyId, caller.ClientId, cancellationToken);
            if (points.All(p => p.Shared == true) && policyEvaluator.CheckEnabled(policy) is { Allowed: false } denied)
            {
                return ApiProblems.Forbidden(
                    denied.Reason!, "Live location sharing is not allowed for this person.", policyService.Describe(policy, device.Timezone));
            }
        }

        var response = await ingestion.SubmitAsync(caller, device, points, policy, cancellationToken);
        return Ok(response);
    }

    private BadRequestObjectResult BadRequestProblem(string detail) =>
        BadRequest(new ProblemDetails { Title = "Invalid request", Detail = detail, Status = StatusCodes.Status400BadRequest });
}
