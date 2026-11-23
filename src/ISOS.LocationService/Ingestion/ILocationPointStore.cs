namespace ISOS.LocationService.Ingestion;

public enum StoreOutcome
{
    /// <summary>The point was accepted: added to history when shared, and now the latest position.</summary>
    Stored,

    /// <summary>The same point was already stored. Nothing changed.</summary>
    Duplicate,

    /// <summary>A not-shared point older than (or as old as) the latest position. Nothing changed.</summary>
    Superseded,
}

public interface ILocationPointStore
{
    /// <summary>
    /// Stores one point (LLD 2.2, 2.3, 5.2).
    /// A shared point is added to history (INSERT ... ON CONFLICT DO NOTHING) and, when newer, becomes the latest
    /// position, in one transaction. A not-shared point never enters history; it only replaces the latest position
    /// when it is newer.
    /// </summary>
    Task<StoreOutcome> StoreAsync(LocationPoint point, CancellationToken cancellationToken);
}
