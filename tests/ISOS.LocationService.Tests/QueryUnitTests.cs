using ISOS.LocationService.Ingestion;
using ISOS.LocationService.Query;
using Microsoft.Extensions.Logging.Abstractions;

namespace ISOS.LocationService.Tests;

public class QueryUnitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    private sealed class FakeSource(string name, string type, params (string Id, int Seconds)[] items) : ITimelineSource
    {
        public string Name => name;

        public Task<IReadOnlyList<NormalizedPoint>> ReadAsync(
            string partyId, DateTimeOffset from, PageCursor? after, int limit, CancellationToken cancellationToken)
        {
            var rank = TimelineTypes.Rank(type);
            IReadOnlyList<NormalizedPoint> result = items
                .Select(i => new NormalizedPoint(i.Id, type, 1, 2, null, null, null, T0.AddSeconds(i.Seconds)))
                .Where(p => after is null
                    || p.Timestamp.UtcTicks > after.Ticks
                    || (p.Timestamp.UtcTicks == after.Ticks && rank > after.Rank))
                .Take(limit)
                .ToList();
            return Task.FromResult(result);
        }
    }

    private sealed class FailingSource : ITimelineSource
    {
        public string Name => TimelineTypes.CheckIn;

        public Task<IReadOnlyList<NormalizedPoint>> ReadAsync(
            string partyId, DateTimeOffset from, PageCursor? after, int limit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("source down");
    }

    private static ReplayService Replay(params ITimelineSource[] sources) =>
        new(sources, NullLogger<ReplayService>.Instance);

    [Fact]
    public async Task Replay_merges_sources_oldest_first_and_breaks_ties_AEC_then_check_in_then_location()
    {
        var replay = Replay(
            new FakeSource("loc", TimelineTypes.LocationShare, ("l1", 0), ("l2", 20)),
            new FakeSource("chk", TimelineTypes.CheckIn, ("c1", 0), ("c2", 10)),
            new FakeSource("aec", TimelineTypes.Aec, ("a1", 0)));

        var page = await replay.ReadAsync("p", T0, null, 10, CancellationToken.None);

        Assert.Equal(["a1", "c1", "l1", "c2", "l2"], page.Points.Select(p => p.Id));
        Assert.Null(page.Next);
        Assert.Empty(page.FailedSources);
    }

    [Fact]
    public async Task Replay_pages_without_skipping_or_repeating_items_that_share_a_timestamp()
    {
        var replay = Replay(
            new FakeSource("loc", TimelineTypes.LocationShare, ("l1", 0), ("l2", 20)),
            new FakeSource("chk", TimelineTypes.CheckIn, ("c1", 0), ("c2", 10)),
            new FakeSource("aec", TimelineTypes.Aec, ("a1", 0)));

        var seen = new List<string>();
        PageCursor? cursor = null;
        do
        {
            var page = await replay.ReadAsync("p", T0, cursor, 2, CancellationToken.None);
            seen.AddRange(page.Points.Select(p => p.Id));
            cursor = page.Next;
        } while (cursor is not null);

        Assert.Equal(["a1", "c1", "l1", "c2", "l2"], seen);
    }

    [Fact]
    public async Task Replay_returns_the_other_sources_when_one_fails()
    {
        var replay = Replay(
            new FakeSource("loc", TimelineTypes.LocationShare, ("l1", 0)),
            new FailingSource());

        var page = await replay.ReadAsync("p", T0, null, 10, CancellationToken.None);

        Assert.Equal(["l1"], page.Points.Select(p => p.Id));
        Assert.Equal([TimelineTypes.CheckIn], page.FailedSources);
    }

    [Fact]
    public async Task Replay_cursor_continues_correctly_through_the_location_source()
    {
        var store = new InMemoryLocationPointStore();
        for (var i = 0; i < 5; i++)
        {
            await store.StoreAsync(NewPoint(T0.AddSeconds(i < 3 ? 0 : i)), CancellationToken.None);
        }

        var replay = Replay(new LocationShareTimelineSource(store));
        var seen = new List<string>();
        PageCursor? cursor = null;
        do
        {
            var page = await replay.ReadAsync("p", T0, cursor, 2, CancellationToken.None);
            seen.AddRange(page.Points.Select(p => p.Id));
            cursor = page.Next;
        } while (cursor is not null);

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Theory]
    [InlineData("1h", 1, 1)]
    [InlineData("24h", 14, 24)]
    [InlineData("14D", 14, 14 * 24)]
    [InlineData(null, 14, 1)]
    [InlineData("14d", 7, 7 * 24)]
    public void Range_is_parsed_and_capped_at_retention(string? value, int retentionDays, int expectedHours)
    {
        Assert.True(HistoryRange.TryResolve(value, retentionDays, out _, out var span));
        Assert.Equal(TimeSpan.FromHours(expectedHours), span);
    }

    [Theory]
    [InlineData("2h")]
    [InlineData("30d")]
    [InlineData("abc")]
    public void Unknown_range_is_rejected(string value) =>
        Assert.False(HistoryRange.TryResolve(value, 14, out _, out _));

    [Fact]
    public void Cursor_round_trips_and_garbage_is_rejected()
    {
        var cursor = new PageCursor(T0.UtcTicks, 2, Guid.NewGuid().ToString());

        Assert.True(PageCursor.TryDecode(cursor.Encode(), out var decoded));
        Assert.Equal(cursor, decoded);
        Assert.False(PageCursor.TryDecode("not-a-cursor", out _));
        Assert.False(PageCursor.TryDecode("MXwyfA", out _)); // "1|2|" has no id
        Assert.True(PageCursor.TryDecode(null, out var none));
        Assert.Null(none);
    }

    private static LocationPoint NewPoint(DateTimeOffset capturedAt) => new(
        Guid.NewGuid(), "p", null, "d", capturedAt, null, capturedAt, 1, 2, 5, null, null, null,
        QualityFlags.None, true, DateOnly.FromDateTime(capturedAt.UtcDateTime).AddDays(14));
}
