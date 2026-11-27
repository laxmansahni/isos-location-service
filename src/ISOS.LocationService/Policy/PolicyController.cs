using System.Globalization;
using ISOS.LocationService.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ISOS.LocationService.Policy;

[ApiController]
[Route("v1/locationtracking")]
public sealed class PolicyController(
    ICallerContext callerContext,
    PolicyService policy,
    IPolicyStore store,
    IOptions<PolicyOptions> options,
    TimeProvider clock) : ControllerBase
{
    private const int MaxIdLength = 100;
    private const int MaxScheduleWindows = 50;
    private const string ClientRequired = "A client is required. In Development send the clientId header.";

    /// <summary>The effective policy for the caller: whether sharing is allowed now and what the device should do.</summary>
    /// <remarks>
    /// The device refreshes this at foreground, login, token refresh, location wake-ups, the heartbeat and
    /// <c>nextEvaluationAt</c> (LLD 3.3). Supports ETag with 304.
    /// </remarks>
    /// <param name="tz">The device's IANA time zone. Used for schedule windows before the person's home zone.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpGet("policy")]
    [ProducesResponseType<PolicyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPolicy([FromQuery] string? tz, CancellationToken cancellationToken)
    {
        if (Caller(out var caller, out var denied) is false)
        {
            return denied!;
        }

        return PolicyResult(await policy.LoadAsync(caller.PartyId, caller.ClientId!, cancellationToken), tz, allowNotModified: true);
    }

    /// <summary>Grants or revokes consent to share live location.</summary>
    /// <remarks>
    /// GRANTED needs the current disclosure version (see <c>disclosureVersion</c> in the policy) and a client that
    /// allows sharing. REVOKED takes effect at once (ADR-005): ingestion returns 403 CONSENT_REQUIRED, the person's
    /// earlier points stop being visible and their latest position is deleted. Repeating a request changes nothing.
    /// Returns the new policy.
    /// </remarks>
    /// <param name="request">The new consent state.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPut("consent")]
    [ProducesResponseType<PolicyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> PutConsent([FromBody] ConsentRequest request, CancellationToken cancellationToken)
    {
        if (Caller(out var caller, out var denied) is false)
        {
            return denied!;
        }

        var now = clock.GetUtcNow();
        switch (request.Status)
        {
            case ConsentStatuses.Granted:
                if (request.DisclosureVersion != options.Value.DisclosureVersion)
                {
                    return ApiProblems.BadRequest($"disclosureVersion must be \"{options.Value.DisclosureVersion}\", the version shown to the person.");
                }

                var before = await policy.LoadAsync(caller.PartyId, caller.ClientId!, cancellationToken);
                if (!before.Client.SharingEnabled)
                {
                    return ApiProblems.Forbidden(PolicyDenials.SharingNotAllowed, "The client has not enabled live location sharing.", policy.Describe(before, null));
                }

                await store.GrantConsentAsync(caller.PartyId, caller.ClientId!, request.DisclosureVersion, now, cancellationToken);
                break;
            case ConsentStatuses.Revoked:
                await store.RevokeConsentAsync(caller.PartyId, caller.ClientId!, now, cancellationToken);
                break;
            default:
                return ApiProblems.BadRequest("status must be GRANTED or REVOKED.");
        }

        return PolicyResult(await policy.LoadAsync(caller.PartyId, caller.ClientId!, cancellationToken), null, allowNotModified: false);
    }

    /// <summary>Reports the phone's location permission and time zone for this device.</summary>
    /// <remarks>
    /// The session is ACTIVE only with the ALWAYS permission and precise location on; otherwise it is
    /// SUSPENDED_OS_PERMISSION (ADR-005). Consent is not affected. Each call renews the sharing lease.
    /// </remarks>
    /// <param name="deviceId">The device id, the same as <c>device.id</c> on points.</param>
    /// <param name="request">The current permission state.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPut("devices/{deviceId}/state")]
    [ProducesResponseType<DeviceStateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> PutDeviceState(
        string deviceId, [FromBody] DeviceStateRequest request, CancellationToken cancellationToken)
    {
        if (Caller(out var caller, out var denied) is false)
        {
            return denied!;
        }

        if (deviceId.Length > MaxIdLength)
        {
            return ApiProblems.BadRequest($"deviceId may be at most {MaxIdLength} characters.");
        }

        if (request.OsPermission is null || !OsPermissions.All.Contains(request.OsPermission))
        {
            return ApiProblems.BadRequest($"osPermission must be one of {string.Join(", ", OsPermissions.All)}.");
        }

        if (request.PreciseLocation is null)
        {
            return ApiProblems.BadRequest("preciseLocation is required.");
        }

        var now = clock.GetUtcNow();
        var lease = now + options.Value.LeaseDuration;
        await store.UpsertDeviceStateAsync(
            new DeviceStateUpdate(
                caller.PartyId, caller.ClientId!, deviceId, request.Platform, request.OsPermission, request.PreciseLocation.Value,
                PolicyEvaluator.TryFindZone(request.Timezone, out _) ? request.Timezone : null, now, lease),
            cancellationToken);

        var state = request.OsPermission == OsPermissions.Always && request.PreciseLocation.Value
            ? DeviceSessionStates.Active
            : DeviceSessionStates.SuspendedOsPermission;
        return Ok(new DeviceStateResponse(state, lease));
    }

    /// <summary>Ends this device's session (logout).</summary>
    /// <remarks>Other devices are not affected (ADR-008). Safe to repeat. Returns 204.</remarks>
    /// <param name="deviceId">The device id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpDelete("devices/{deviceId}/session")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> DeleteDeviceSession(string deviceId, CancellationToken cancellationToken)
    {
        if (Caller(out var caller, out var denied) is false)
        {
            return denied!;
        }

        if (deviceId.Length > MaxIdLength)
        {
            return ApiProblems.BadRequest($"deviceId may be at most {MaxIdLength} characters.");
        }

        await store.EndDeviceSessionAsync(caller.PartyId, caller.ClientId!, deviceId, cancellationToken);
        return NoContent();
    }

    /// <summary>Sets the person's sharing type, home time zone and schedule.</summary>
    /// <remarks>
    /// The type must be one the client offers. SCHEDULE needs at least one weekly window (local time, day 0 =
    /// Sunday; an end before the start crosses midnight). The schedule is kept when another type is chosen, and a
    /// change takes effect immediately. Returns the new policy.
    /// </remarks>
    /// <param name="request">The preference.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpPut("users/me/preference")]
    [ProducesResponseType<PolicyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> PutPreference([FromBody] PreferenceRequest request, CancellationToken cancellationToken)
    {
        if (Caller(out var caller, out var denied) is false)
        {
            return denied!;
        }

        if (request.PrimaryType is null || !SharingTypes.Primary.Contains(request.PrimaryType))
        {
            return ApiProblems.BadRequest($"primaryType must be one of {string.Join(", ", SharingTypes.Primary)}.");
        }

        if (request.HomeTimezone is not null && !PolicyEvaluator.TryFindZone(request.HomeTimezone, out _))
        {
            return ApiProblems.BadRequest("homeTimezone must be an IANA time zone such as Europe/London.");
        }

        if (!TryParseSchedule(request.Schedule, out var windows, out var scheduleError))
        {
            return ApiProblems.BadRequest(scheduleError!);
        }

        if (request.PrimaryType == SharingTypes.Schedule && windows.Count == 0)
        {
            return ApiProblems.BadRequest("The SCHEDULE type needs at least one schedule window.");
        }

        var snapshot = await policy.LoadAsync(caller.PartyId, caller.ClientId!, cancellationToken);
        if (!snapshot.Client.SharingEnabled)
        {
            return ApiProblems.Forbidden(PolicyDenials.SharingNotAllowed, "The client has not enabled live location sharing.", policy.Describe(snapshot, null));
        }

        if (!snapshot.Client.IsEnabled(request.PrimaryType))
        {
            return ApiProblems.Forbidden("TYPE_NOT_ENABLED", $"The client does not offer {request.PrimaryType}.", policy.Describe(snapshot, null));
        }

        await store.SavePreferenceAsync(
            caller.PartyId, caller.ClientId!, new SharingPreference(request.PrimaryType, request.HomeTimezone, windows), clock.GetUtcNow(), cancellationToken);

        return PolicyResult(await policy.LoadAsync(caller.PartyId, caller.ClientId!, cancellationToken), null, allowNotModified: false);
    }

    private IActionResult PolicyResult(PolicySnapshot snapshot, string? tz, bool allowNotModified)
    {
        var response = policy.Describe(snapshot, tz);
        if (allowNotModified && Request.Headers.IfNoneMatch.ToString().Split(',', StringSplitOptions.TrimEntries).Contains(response.Etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.ETag = response.Etag;
        return Ok(response);
    }

    // The policy is per (person, client), so both are needed.
    private bool Caller(out CallerIdentity caller, out IActionResult? denied)
    {
        caller = callerContext.Current ?? new CallerIdentity(string.Empty);
        if (callerContext.Current is null)
        {
            denied = Unauthorized();
            return false;
        }

        if (caller.ClientId is null)
        {
            denied = ApiProblems.BadRequest(ClientRequired);
            return false;
        }

        denied = null;
        return true;
    }

    private static bool TryParseSchedule(IReadOnlyList<ScheduleWindowDto>? input, out List<ScheduleWindow> windows, out string? error)
    {
        windows = [];
        error = null;
        if (input is null)
        {
            return true;
        }

        if (input.Count > MaxScheduleWindows)
        {
            error = $"A schedule may have at most {MaxScheduleWindows} windows.";
            return false;
        }

        foreach (var w in input)
        {
            if (w.DayOfWeek is not (>= 0 and <= 6)
                || !TimeOnly.TryParseExact(w.StartTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                || !TimeOnly.TryParseExact(w.EndTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
                || start == end)
            {
                error = "Each schedule window needs dayOfWeek 0 to 6, a startTime and an endTime as HH:mm, and the two times must differ.";
                return false;
            }

            windows.Add(new ScheduleWindow(w.DayOfWeek.Value, start, end));
        }

        return true;
    }
}
