namespace ISOS.LocationService.Query;

/// <summary>Closed set of point types the read model can return (LLD 2.4). The backend never returns icon URLs.</summary>
public static class TimelineTypes
{
    public const string Aec = "AEC";
    public const string CheckIn = "CHECK_IN";
    public const string LocationShare = "LOCATION_SHARE";

    public static readonly IReadOnlyList<string> All = [Aec, CheckIn, LocationShare];

    /// <summary>Order of items with the same timestamp in a replay: AEC, then CHECK_IN, then LOCATION_SHARE (LLD 4.4).</summary>
    public static int Rank(string type) => type switch
    {
        Aec => 0,
        CheckIn => 1,
        _ => 2,
    };
}

/// <param name="Id">Point id (a location share) or the source's own id.</param>
/// <param name="Type">One of <see cref="TimelineTypes"/>.</param>
/// <param name="Latitude">Degrees.</param>
/// <param name="Longitude">Degrees.</param>
/// <param name="AccuracyM">Null when the source does not have it.</param>
/// <param name="SpeedMps">Null when unknown.</param>
/// <param name="CourseDeg">Degrees clockwise from true north. Null when unknown.</param>
/// <param name="Timestamp">When the point was captured.</param>
public sealed record NormalizedPoint(
    string Id,
    string Type,
    double Latitude,
    double Longitude,
    double? AccuracyM,
    double? SpeedMps,
    double? CourseDeg,
    DateTimeOffset Timestamp);

/// <param name="UserId">Party id.</param>
/// <param name="FirstName">Null until the user directory is wired in.</param>
/// <param name="LastName">Null until the user directory is wired in.</param>
/// <param name="Latitude">Degrees.</param>
/// <param name="Longitude">Degrees.</param>
/// <param name="CapturedAt">When the device captured the point.</param>
/// <param name="FreshnessSeconds">Age of the point at the time of the request.</param>
public sealed record LatestItem(
    string UserId,
    string? FirstName,
    string? LastName,
    double Latitude,
    double Longitude,
    DateTimeOffset CapturedAt,
    int FreshnessSeconds);

/// <param name="Items">Newest first.</param>
/// <param name="NextSince">Pass as <c>since</c> on the next poll to receive only newer points.</param>
public sealed record LatestResponse(IReadOnlyList<LatestItem> Items, DateTimeOffset? NextSince);

/// <param name="UserId">Party id of the person.</param>
/// <param name="Range">The requested range, one of 1h, 24h, 7d, 14d (capped at the retention window).</param>
/// <param name="From">Start of the window actually applied.</param>
/// <param name="Points">Newest first.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> to get the next page. Null on the last page.</param>
public sealed record HistoryResponse(
    string UserId,
    string Range,
    DateTimeOffset From,
    IReadOnlyList<NormalizedPoint> Points,
    string? NextCursor);

/// <param name="UserId">Party id of the person.</param>
/// <param name="Range">The requested range, one of 1h, 24h, 7d, 14d (capped at the retention window).</param>
/// <param name="From">Start of the window actually applied.</param>
/// <param name="Points">Oldest first.</param>
/// <param name="Partial">True when a source failed; the other sources are still returned.</param>
/// <param name="FailedSources">Names of the sources that failed.</param>
/// <param name="NextCursor">Pass as <c>cursor</c> to get the next page. Null on the last page.</param>
public sealed record ReplayResponse(
    string UserId,
    string Range,
    DateTimeOffset From,
    IReadOnlyList<NormalizedPoint> Points,
    bool Partial,
    IReadOnlyList<string> FailedSources,
    string? NextCursor);
