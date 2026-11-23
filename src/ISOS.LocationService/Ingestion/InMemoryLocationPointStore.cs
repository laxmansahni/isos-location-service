namespace ISOS.LocationService.Ingestion;

/// <summary>Temporary store for Development and tests. Same rules as the PostgreSQL store; not durable.</summary>
public sealed class InMemoryLocationPointStore : ILocationPointStore
{
    private readonly object _gate = new();
    private readonly HashSet<(string PartyId, DateTimeOffset CapturedAt, Guid PointId)> _history = [];
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
                if (!_history.Add((point.PartyId, point.CapturedAt, point.PointId)))
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
