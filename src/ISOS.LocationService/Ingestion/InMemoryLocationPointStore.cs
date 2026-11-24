using ISOS.LocationService.Query;

namespace ISOS.LocationService.Ingestion;

/// <summary>Temporary store for Development and tests. Same rules as the PostgreSQL store; not durable.</summary>
public sealed class InMemoryLocationPointStore : ILocationPointStore, ILocationQueryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string PartyId, DateTimeOffset CapturedAt, Guid PointId), LocationPoint> _history = [];
    private readonly Dictionary<string, LocationPoint> _latest = [];

    /// <summary>Number of points in history (shared points only).</summary>
    public int HistoryCount
    {
        get { lock (_gate) { return _history.Count; } }
    }

    public LocationPoint? Latest(string partyId)
    {
        lock (_gate) { return _latest.GetValueOrDefault(partyId); }
    }

    public Task<StoreOutcome> StoreAsync(LocationPoint point, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (point.Shared)
            {
                if (!_history.TryAdd((point.PartyId, point.CapturedAt, point.PointId), point))
                {
                    return Task.FromResult(StoreOutcome.Duplicate);
                }

                ReplaceLatestIfNewer(point);
                return Task.FromResult(StoreOutcome.Stored);
            }

            if (ReplaceLatestIfNewer(point))
            {
                return Task.FromResult(StoreOutcome.Stored);
            }

            var current = _latest[point.PartyId];
            return Task.FromResult(current.PointId == point.PointId && current.CapturedAt == point.CapturedAt
                ? StoreOutcome.Duplicate
                : StoreOutcome.Superseded);
        }
    }

    public Task<IReadOnlyList<LocationPoint>> GetLatestSharedAsync(LatestQuery query, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<LocationPoint> result = _latest.Values
                .Where(p => p.Shared
                    && p.CapturedAt >= query.NotOlderThan
                    && (query.Since is null || p.CapturedAt > query.Since)
                    && (query.ClientId is null || p.ClientId == query.ClientId))
                .OrderByDescending(p => p.CapturedAt)
                .ThenBy(p => p.PartyId, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<LocationPoint>> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var rows = _history.Values.Where(p => p.PartyId == query.PartyId && p.Shared && p.CapturedAt >= query.From);
            if (query.After is { } after)
            {
                rows = rows.Where(p => query.NewestFirst
                    ? Compare(p, after) < 0
                    : Compare(p, after) > 0);
            }

            IReadOnlyList<LocationPoint> result = (query.NewestFirst
                    ? rows.OrderByDescending(p => p.CapturedAt).ThenByDescending(p => p.PointId)
                    : rows.OrderBy(p => p.CapturedAt).ThenBy(p => p.PointId))
                .Take(query.Limit)
                .ToList();
            return Task.FromResult(result);
        }
    }

    private static int Compare(LocationPoint point, HistoryCursor cursor)
    {
        var byTime = point.CapturedAt.CompareTo(cursor.At);
        return byTime != 0 ? byTime : point.PointId.CompareTo(cursor.PointId);
    }

    private bool ReplaceLatestIfNewer(LocationPoint point)
    {
        if (_latest.TryGetValue(point.PartyId, out var current) && point.CapturedAt <= current.CapturedAt)
        {
            return false;
        }

        _latest[point.PartyId] = point;
        return true;
    }
}
