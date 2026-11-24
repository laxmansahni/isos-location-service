using ISOS.LocationService.Audit;
using ISOS.LocationService.Database;
using ISOS.LocationService.Ingestion;
using ISOS.LocationService.Query;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ISOS.LocationService.Tests;

public class PostgresQueryTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public PostgresQueryTests(PostgresFixture postgres) => _postgres = postgres;

    private static LocationPoint Point(string party, DateTimeOffset capturedAt, bool shared = true, string? client = null, Guid? pointId = null) => new(
        PointId: pointId ?? Guid.NewGuid(),
        PartyId: party,
        ClientId: client,
        DeviceId: "device-1",
        CapturedAt: capturedAt,
        UtcOffsetMinutes: 330,
        ReceivedAt: capturedAt.AddSeconds(5),
        Latitude: 28.6139,
        Longitude: 77.2090,
        AccuracyMeters: 12.5,
        SpeedMetersPerSecond: 1.4,
        CourseDegrees: null,
        AltitudeMeters: null,
        Flags: QualityFlags.None,
        Shared: shared,
        RetentionExpiresAt: DateOnly.FromDateTime(capturedAt.UtcDateTime).AddDays(14));

    private async Task<(PostgresLocationPointStore Writer, PostgresLocationQueryStore Reader, NpgsqlDataSource DataSource)> PrepareAsync()
    {
        DatabaseMigrator.Migrate(_postgres.ConnectionString, NullLogger.Instance);
        var dataSource = NpgsqlDataSource.Create(_postgres.ConnectionString);
        await new PartitionMaintenance(dataSource, Options.Create(new IngestionOptions()), TimeProvider.System)
            .EnsureAsync(CancellationToken.None);
        return (new PostgresLocationPointStore(dataSource), new PostgresLocationQueryStore(dataSource), dataSource);
    }

    [Fact]
    public async Task History_is_ordered_and_paged_by_time_then_point_id()
    {
        var (writer, reader, _) = await PrepareAsync();
        var party = $"IND_{Guid.NewGuid()}";
        var now = DateTimeOffset.UtcNow;
        var sameTime = now.AddMinutes(-20);
        foreach (var capturedAt in new[] { now.AddMinutes(-30), sameTime, sameTime, now.AddMinutes(-10) })
        {
            await writer.StoreAsync(Point(party, capturedAt), CancellationToken.None);
        }

        var all = await reader.GetHistoryAsync(new HistoryQuery(party, now.AddHours(-1), null, NewestFirst: false, 10), CancellationToken.None);
        Assert.Equal(4, all.Count);
        Assert.Equal(all.OrderBy(p => p.CapturedAt).Select(p => p.CapturedAt), all.Select(p => p.CapturedAt));

        foreach (var newestFirst in new[] { false, true })
        {
            var seen = new List<Guid>();
            HistoryCursor? cursor = null;
            while (true)
            {
                var page = await reader.GetHistoryAsync(new HistoryQuery(party, now.AddHours(-1), cursor, newestFirst, 1), CancellationToken.None);
                if (page.Count == 0)
                {
                    break;
                }

                seen.Add(page[0].PointId);
                cursor = new HistoryCursor(page[0].CapturedAt, page[0].PointId);
            }

            Assert.Equal(4, seen.Distinct().Count());
        }
    }

    [Fact]
    public async Task History_respects_the_range_start_and_reads_values_back_exactly()
    {
        var (writer, reader, _) = await PrepareAsync();
        var party = $"IND_{Guid.NewGuid()}";
        var now = DateTimeOffset.UtcNow;
        await writer.StoreAsync(Point(party, now.AddHours(-5)), CancellationToken.None);
        await writer.StoreAsync(Point(party, now.AddMinutes(-5)), CancellationToken.None);

        var rows = await reader.GetHistoryAsync(new HistoryQuery(party, now.AddHours(-1), null, true, 10), CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(1.4, row.SpeedMetersPerSecond);
        Assert.Equal(12.5, row.AccuracyMeters);
        Assert.Null(row.CourseDegrees);
        Assert.Equal(28.6139, row.Latitude);
    }

    [Fact]
    public async Task Latest_returns_only_fresh_shared_rows_for_the_client()
    {
        var (writer, reader, _) = await PrepareAsync();
        var client = $"client-{Guid.NewGuid()}";
        var now = DateTimeOffset.UtcNow;
        var shared = $"IND_{Guid.NewGuid()}";
        var notShared = $"IND_{Guid.NewGuid()}";
        var stale = $"IND_{Guid.NewGuid()}";
        var otherClient = $"IND_{Guid.NewGuid()}";
        await writer.StoreAsync(Point(shared, now.AddMinutes(-2), client: client), CancellationToken.None);
        await writer.StoreAsync(Point(notShared, now.AddMinutes(-1), shared: false, client: client), CancellationToken.None);
        await writer.StoreAsync(Point(stale, now.AddHours(-3), client: client), CancellationToken.None);
        await writer.StoreAsync(Point(otherClient, now.AddMinutes(-1), client: "someone-else"), CancellationToken.None);

        var rows = await reader.GetLatestSharedAsync(new LatestQuery(now.AddMinutes(-30), null, client), CancellationToken.None);
        var delta = await reader.GetLatestSharedAsync(new LatestQuery(now.AddMinutes(-30), now.AddMinutes(-1), client), CancellationToken.None);

        Assert.Equal([shared], rows.Select(r => r.PartyId));
        Assert.Empty(delta);
    }

    [Fact]
    public async Task Audit_entry_is_stored_in_the_migrated_table()
    {
        var (_, _, dataSource) = await PrepareAsync();
        var actor = $"IND_{Guid.NewGuid()}";

        await new PostgresAccessAuditStore(dataSource).RecordAsync(
            new AccessAuditEntry(DateTimeOffset.UtcNow, actor, "IND_target", "HISTORY", "24h"), CancellationToken.None);

        Assert.Equal(1L, await _postgres.ScalarAsync<long>(
            $"SELECT count(*) FROM access_audit WHERE actor_party_id = '{actor}' AND target_party_id = 'IND_target' AND data_type = 'HISTORY' AND range_requested = '24h'"));
    }
}
