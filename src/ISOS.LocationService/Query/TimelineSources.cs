using ISOS.LocationService.Ingestion;

namespace ISOS.LocationService.Query;

/// <summary>
/// One of the stores a replay merges (LLD 4.4). Items come back oldest first, strictly after <c>after</c>, so a page
/// can be requested from any cursor. Check-ins and AECs belong to other services and will be added as further sources.
/// </summary>
public interface ITimelineSource
{
    /// <summary>Reported in <c>failedSources</c> when this source fails.</summary>
    string Name { get; }

    Task<IReadOnlyList<NormalizedPoint>> ReadAsync(
        string partyId, DateTimeOffset from, PageCursor? after, int limit, CancellationToken cancellationToken);
}

public sealed class LocationShareTimelineSource(ILocationQueryStore store) : ITimelineSource
{
    private static readonly int Rank = TimelineTypes.Rank(TimelineTypes.LocationShare);

    // The largest uuid, so "(time, MaxPointId) is after (time, anything)" in the store's ordering.
    private static readonly Guid MaxPointId = new(Enumerable.Repeat((byte)0xFF, 16).ToArray());

    // The store keeps microseconds, so a step back of one microsecond means "including that timestamp".
    private static readonly TimeSpan Microsecond = TimeSpan.FromTicks(10);

    public string Name => TimelineTypes.LocationShare;

    public async Task<IReadOnlyList<NormalizedPoint>> ReadAsync(
        string partyId, DateTimeOffset from, PageCursor? after, int limit, CancellationToken cancellationToken)
    {
        var points = await store.GetHistoryAsync(
            new HistoryQuery(partyId, from, ToHistoryCursor(after), NewestFirst: false, limit), cancellationToken);
        return points.Select(LocationShareMapping.ToNormalized).ToList();
    }

    // At the cursor's timestamp, items of a type that ranks before the cursor's type were already shown and items
    // of a type that ranks after it were not.
    private static HistoryCursor? ToHistoryCursor(PageCursor? after) => after switch
    {
        null => null,
        _ when after.Rank == Rank => new HistoryCursor(after.At, Guid.TryParse(after.Id, out var id) ? id : MaxPointId),
        _ when after.Rank < Rank => new HistoryCursor(after.At - Microsecond, MaxPointId),
        _ => new HistoryCursor(after.At, MaxPointId),
    };
}

public static class LocationShareMapping
{
    public static NormalizedPoint ToNormalized(LocationPoint p) => new(
        p.PointId.ToString(),
        TimelineTypes.LocationShare,
        p.Latitude,
        p.Longitude,
        p.AccuracyMeters,
        p.SpeedMetersPerSecond,
        p.CourseDegrees,
        p.CapturedAt);
}
