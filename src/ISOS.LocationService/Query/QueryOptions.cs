namespace ISOS.LocationService.Query;

public sealed class QueryOptions
{
    public const string SectionName = "Query";

    public int DefaultPageSize { get; set; } = 500;

    public int MaxPageSize { get; set; } = 1000;

    /// <summary>
    /// A person appears on the map only while their latest shared point is newer than this. Default: the 12 h
    /// heartbeat of the Significant-Change Location Capture story plus 1 h margin (the 13 h candidate in HLD v0.2),
    /// so a stationary person who is still sharing stays visible. Product still has to confirm the map freshness
    /// target (HLD Q7, Q18); "sharing now" cannot be evaluated until policy exists.
    /// </summary>
    public TimeSpan LatestFreshness { get; set; } = TimeSpan.FromHours(13);
}
