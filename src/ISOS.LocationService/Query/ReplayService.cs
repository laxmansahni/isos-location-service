namespace ISOS.LocationService.Query;

public sealed record ReplayPage(IReadOnlyList<NormalizedPoint> Points, PageCursor? Next, IReadOnlyList<string> FailedSources);

/// <summary>
/// Reads every timeline source in parallel, normalises and merge-sorts the result (LLD 4.4). A failing source does
/// not fail the replay: the rest is returned and the failed source is named.
/// </summary>
public sealed class ReplayService(IEnumerable<ITimelineSource> sources, ILogger<ReplayService> logger)
{
    public async Task<ReplayPage> ReadAsync(
        string partyId, DateTimeOffset from, PageCursor? after, int limit, CancellationToken cancellationToken)
    {
        // One extra item per source tells us whether another page exists.
        var reads = await Task.WhenAll(sources.Select(source => ReadSourceAsync(source, partyId, from, after, limit + 1, cancellationToken)));

        // OrderBy is stable, so items of one source keep the order the source returned them in.
        var merged = reads
            .SelectMany(r => r.Items)
            .OrderBy(p => p.Timestamp)
            .ThenBy(p => TimelineTypes.Rank(p.Type))
            .ToList();

        var page = merged.Take(limit).ToList();
        var next = merged.Count > limit ? PageCursor.After(page[^1]) : null;
        return new ReplayPage(page, next, reads.Where(r => r.Failed).Select(r => r.Name).ToList());
    }

    private async Task<(string Name, IReadOnlyList<NormalizedPoint> Items, bool Failed)> ReadSourceAsync(
        ITimelineSource source, string partyId, DateTimeOffset from, PageCursor? after, int limit, CancellationToken cancellationToken)
    {
        try
        {
            return (source.Name, await source.ReadAsync(partyId, from, after, limit, cancellationToken), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Replay source {Source} failed", source.Name);
            return (source.Name, [], true);
        }
    }
}
