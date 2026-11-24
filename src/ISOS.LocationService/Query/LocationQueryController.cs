using System.Security.Cryptography;
using System.Text;
using ISOS.LocationService.Audit;
using ISOS.LocationService.Identity;
using ISOS.LocationService.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ISOS.LocationService.Query;

[ApiController]
[Route("v1/locationtracking")]
public sealed class LocationQueryController(
    ICallerContext callerContext,
    ILocationQueryStore store,
    ReplayService replay,
    IAccessAuditStore audit,
    IOptions<IngestionOptions> ingestionOptions,
    IOptions<QueryOptions> queryOptions,
    TimeProvider clock) : ControllerBase
{
    /// <summary>Latest shared position of each person, for the manager map.</summary>
    /// <remarks>
    /// Needs the MGRLOCHIS function code. Returns only LOCATION_SHARE points that are shared and fresh; a person whose
    /// newest point is not shared does not appear. Supports ETag with 304 and a <c>since</c> cursor for delta polling
    /// (LLD 4.3). Population scope and names from the user directory are not built yet.
    /// </remarks>
    /// <param name="clientId">Limits the result to one client. Ignored when the caller's client is known.</param>
    /// <param name="types">Comma separated list of LOCATION_SHARE, CHECK_IN, AEC. Only LOCATION_SHARE is returned by this layer.</param>
    /// <param name="since">Return only points captured after this time (use <c>nextSince</c> from the previous response).</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpGet("latest")]
    [ProducesResponseType<LatestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetLatest(
        [FromQuery] string? clientId, [FromQuery] string? types, [FromQuery] DateTimeOffset? since, CancellationToken cancellationToken)
    {
        if (callerContext.Current is not { } caller)
        {
            return Unauthorized();
        }

        if (!caller.Has(LocationPermissions.ManagerHistory))
        {
            return Forbidden("FORBIDDEN", "The caller may not view the live map.");
        }

        var wanted = string.IsNullOrWhiteSpace(types)
            ? TimelineTypes.All
            : types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (wanted.Any(t => !TimelineTypes.All.Contains(t)))
        {
            return BadRequestProblem($"types must be a list of {string.Join(", ", TimelineTypes.All)}.");
        }

        var now = clock.GetUtcNow();
        IReadOnlyList<LocationPoint> points = wanted.Contains(TimelineTypes.LocationShare)
            ? await store.GetLatestSharedAsync(
                new LatestQuery(now - queryOptions.Value.LatestFreshness, since, caller.ClientId ?? clientId), cancellationToken)
            : [];

        // Derived from who is shown and which point, so it changes when anyone moves or drops off the map.
        var etag = ComputeEtag(points);
        if (Request.Headers.IfNoneMatch.ToString().Split(',', StringSplitOptions.TrimEntries).Contains(etag))
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.ETag = etag;
        var items = points
            .Select(p => new LatestItem(
                p.PartyId, null, null, p.Latitude, p.Longitude, p.CapturedAt, (int)Math.Max(0, (now - p.CapturedAt).TotalSeconds)))
            .ToList();
        DateTimeOffset? nextSince = points.Count > 0 ? points.Max(p => p.CapturedAt) : since;
        return Ok(new LatestResponse(items, nextSince));
    }

    /// <summary>A person's shared location history, newest first.</summary>
    /// <remarks>
    /// Own history needs USRLOCHIS; another person's needs MGRLOCHIS (population scope is not enforced yet) and is
    /// audited. Only LOCATION_SHARE points are returned, never not-shared ones. An empty result is 200 with no points.
    /// </remarks>
    /// <param name="id">Party id of the person.</param>
    /// <param name="range">1h (default), 24h, 7d or 14d, capped at the retention window.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, capped by the server.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpGet("users/{id}/history")]
    [ProducesResponseType<HistoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetHistory(
        string id, [FromQuery] string? range, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (Authorize(id, out var caller) is { } denied)
        {
            return denied;
        }

        if (Parse(range, cursor, limit) is not { } query
            || !TryHistoryCursor(query.After, out var after))
        {
            return BadRequestProblem(InvalidQueryDetail);
        }

        await AuditIfOtherPersonAsync(caller, id, "HISTORY", query.RangeName, cancellationToken);

        var rows = await store.GetHistoryAsync(
            new HistoryQuery(id, query.From, after, NewestFirst: true, query.Limit + 1), cancellationToken);
        var page = rows.Take(query.Limit).Select(LocationShareMapping.ToNormalized).ToList();
        var next = rows.Count > query.Limit ? PageCursor.After(page[^1]).Encode() : null;
        return Ok(new HistoryResponse(id, query.RangeName, query.From, page, next));
    }

    /// <summary>A person's timeline: location shares merged with check-ins and AECs, oldest first.</summary>
    /// <remarks>
    /// Same authorisation and audit as history. Sources are read in parallel; when one fails the others are still
    /// returned with <c>partial: true</c> and the failed <c>failedSources</c>. Only the location share source exists
    /// so far; check-ins and AECs come from other services.
    /// </remarks>
    /// <param name="id">Party id of the person.</param>
    /// <param name="range">1h (default), 24h, 7d or 14d, capped at the retention window.</param>
    /// <param name="cursor">The <c>nextCursor</c> of the previous page.</param>
    /// <param name="limit">Page size, capped by the server.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    [HttpGet("users/{id}/replay")]
    [ProducesResponseType<ReplayResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetReplay(
        string id, [FromQuery] string? range, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (Authorize(id, out var caller) is { } denied)
        {
            return denied;
        }

        if (Parse(range, cursor, limit) is not { } query)
        {
            return BadRequestProblem(InvalidQueryDetail);
        }

        await AuditIfOtherPersonAsync(caller, id, "REPLAY", query.RangeName, cancellationToken);

        var result = await replay.ReadAsync(id, query.From, query.After, query.Limit, cancellationToken);
        return Ok(new ReplayResponse(
            id, query.RangeName, query.From, result.Points, result.FailedSources.Count > 0, result.FailedSources, result.Next?.Encode()));
    }

    private const string InvalidQueryDetail = "range must be 1h, 24h, 7d or 14d; cursor must be one returned by this API; limit must be positive.";

    private sealed record ParsedQuery(string RangeName, DateTimeOffset From, PageCursor? After, int Limit);

    private ParsedQuery? Parse(string? range, string? cursor, int? limit)
    {
        if (!HistoryRange.TryResolve(range, ingestionOptions.Value.RetentionDays, out var name, out var span)
            || !PageCursor.TryDecode(cursor, out var after)
            || limit is <= 0)
        {
            return null;
        }

        var options = queryOptions.Value;
        return new ParsedQuery(name, clock.GetUtcNow() - span, after, Math.Min(limit ?? options.DefaultPageSize, options.MaxPageSize));
    }

    // A history cursor always points at a location share, so anything else was not issued by this endpoint.
    private static bool TryHistoryCursor(PageCursor? cursor, out HistoryCursor? result)
    {
        result = null;
        if (cursor is null)
        {
            return true;
        }

        if (cursor.Rank != TimelineTypes.Rank(TimelineTypes.LocationShare) || !Guid.TryParse(cursor.Id, out var pointId))
        {
            return false;
        }

        result = new HistoryCursor(cursor.At, pointId);
        return true;
    }

    // Own data needs the member code, anyone else's the manager code (LLD 4.4). Returns null when allowed.
    private IActionResult? Authorize(string targetPartyId, out CallerIdentity caller)
    {
        caller = callerContext.Current ?? new CallerIdentity(string.Empty);
        if (callerContext.Current is null)
        {
            return Unauthorized();
        }

        var required = caller.PartyId == targetPartyId ? LocationPermissions.UserHistory : LocationPermissions.ManagerHistory;
        return caller.Has(required)
            ? null
            : Forbidden("HISTORY_NOT_PERMITTED", "The caller may not view this location history.");
    }

    // Audited before any data is read, so a failed audit write means nothing is returned.
    private Task AuditIfOtherPersonAsync(CallerIdentity caller, string targetPartyId, string dataType, string range, CancellationToken cancellationToken) =>
        caller.PartyId == targetPartyId
            ? Task.CompletedTask
            : audit.RecordAsync(new AccessAuditEntry(clock.GetUtcNow(), caller.PartyId, targetPartyId, dataType, range), cancellationToken);

    private static string ComputeEtag(IReadOnlyList<LocationPoint> points)
    {
        var text = string.Join('|', points.Select(p => $"{p.PartyId}:{p.PointId}"));
        return $"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32]}\"";
    }

    private ObjectResult Forbidden(string code, string detail)
    {
        var problem = new ProblemDetails { Title = code, Detail = detail, Status = StatusCodes.Status403Forbidden };
        problem.Extensions["code"] = code;
        return StatusCode(StatusCodes.Status403Forbidden, problem);
    }

    private BadRequestObjectResult BadRequestProblem(string detail) =>
        BadRequest(new ProblemDetails { Title = "Invalid request", Detail = detail, Status = StatusCodes.Status400BadRequest });
}
