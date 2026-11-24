using ISOS.LocationService.Ingestion;

namespace ISOS.LocationService.Query;

/// <param name="NotOlderThan">The freshness threshold: older points are not returned.</param>
/// <param name="Since">Delta polling: only points captured after this.</param>
/// <param name="ClientId">Tenant filter.</param>
public sealed record LatestQuery(DateTimeOffset NotOlderThan, DateTimeOffset? Since, string? ClientId);

/// <summary>Position inside the history table's order: capture time, then point id.</summary>
public sealed record HistoryCursor(DateTimeOffset At, Guid PointId);

public sealed record HistoryQuery(
    string PartyId,
    DateTimeOffset From,
    HistoryCursor? After,
    bool NewestFirst,
    int Limit);

public interface ILocationQueryStore
{
    /// <summary>
    /// Latest position per person, shared points only (LLD 2.3: a not-shared point is never shown to managers).
    /// Newest first.
    /// </summary>
    Task<IReadOnlyList<LocationPoint>> GetLatestSharedAsync(LatestQuery query, CancellationToken cancellationToken);

    /// <summary>One page of a person's shared history, from <see cref="HistoryQuery.From"/> to now.</summary>
    Task<IReadOnlyList<LocationPoint>> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken);
}
