using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ISOS.LocationService.Database;
using ISOS.LocationService.Identity;
using ISOS.LocationService.Ingestion;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ISOS.LocationService.Tests;

public class PostgresStoreTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public PostgresStoreTests(PostgresFixture postgres) => _postgres = postgres;

    private static LocationPoint Point(DateTimeOffset capturedAt, Guid? pointId = null, string party = "IND_test-1", bool shared = true) => new(
        PointId: pointId ?? Guid.NewGuid(),
        PartyId: party,
        ClientId: null,
        DeviceId: "device-1",
        CapturedAt: capturedAt,
        UtcOffsetMinutes: 330,
        ReceivedAt: capturedAt.AddSeconds(5),
        Latitude: 28.6139,
        Longitude: 77.2090,
        AccuracyMeters: 12.5,
        SpeedMetersPerSecond: 1.4,
        CourseDegrees: 270,
        AltitudeMeters: null,
        Flags: QualityFlags.MockLocation,
        Shared: shared,
        RetentionExpiresAt: DateOnly.FromDateTime(capturedAt.UtcDateTime).AddDays(14));

    private async Task<(PostgresLocationPointStore Store, NpgsqlDataSource DataSource)> PrepareAsync()
    {
        DatabaseMigrator.Migrate(_postgres.ConnectionString, NullLogger.Instance);
        var dataSource = NpgsqlDataSource.Create(_postgres.ConnectionString);
        var maintenance = new PartitionMaintenance(dataSource, Options.Create(new IngestionOptions()), TimeProvider.System);
        await maintenance.EnsureAsync(CancellationToken.None);
        return (new PostgresLocationPointStore(dataSource), dataSource);
    }

    [Fact]
    public async Task Migration_is_repeatable()
    {
        DatabaseMigrator.Migrate(_postgres.ConnectionString, NullLogger.Instance);
        DatabaseMigrator.Migrate(_postgres.ConnectionString, NullLogger.Instance);

        Assert.True(await _postgres.ScalarAsync<bool>("SELECT to_regclass('location_history') IS NOT NULL"));
    }

    [Fact]
    public async Task Partitions_cover_the_retention_window_and_a_week_ahead()
    {
        var (_, dataSource) = await PrepareAsync();
        await using var _ = dataSource;

        // 15 days back, today and 7 days ahead.
        var count = await _postgres.ScalarAsync<long>(
            "SELECT count(*) FROM pg_inherits WHERE inhparent = 'location_history'::regclass");
        Assert.True(count >= 23);
    }

    [Fact]
    public async Task Ensure_is_idempotent()
    {
        var (_, dataSource) = await PrepareAsync();
        await using var _ = dataSource;
        var maintenance = new PartitionMaintenance(dataSource, Options.Create(new IngestionOptions()), TimeProvider.System);

        Assert.Equal(0, await maintenance.EnsureAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Point_is_stored_with_every_column()
    {
        var (store, dataSource) = await PrepareAsync();
        await using var _ = dataSource;
        var point = Point(DateTimeOffset.UtcNow.AddMinutes(-10));

        Assert.Equal(StoreOutcome.Stored, await store.StoreAsync(point, CancellationToken.None));

        await using var command = dataSource.CreateCommand(
            "SELECT party_id, device_id, utc_offset_min, latitude, accuracy_m, speed_mps, course_deg, altitude_m, quality_flags, retention_expires_at " +
            "FROM location_history WHERE point_id = @id");
        command.Parameters.AddWithValue("id", point.PointId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("IND_test-1", reader.GetString(0));
        Assert.Equal("device-1", reader.GetString(1));
        Assert.Equal((short)330, reader.GetInt16(2));
        Assert.Equal(28.6139, reader.GetDouble(3), 6);
        Assert.Equal(12.5f, reader.GetFloat(4));
        Assert.Equal(1.4f, reader.GetFloat(5));
        Assert.Equal(270f, reader.GetFloat(6));
        Assert.True(reader.IsDBNull(7));
        Assert.Equal((int)QualityFlags.MockLocation, reader.GetInt32(8));
        Assert.Equal(point.RetentionExpiresAt, DateOnly.FromDateTime(reader.GetDateTime(9)));
    }

    [Fact]
    public async Task Same_point_is_a_duplicate_and_leaves_one_row()
    {
        var (store, dataSource) = await PrepareAsync();
        await using var _ = dataSource;
        var point = Point(DateTimeOffset.UtcNow.AddMinutes(-20));

        Assert.Equal(StoreOutcome.Stored, await store.StoreAsync(point, CancellationToken.None));
        Assert.Equal(StoreOutcome.Duplicate, await store.StoreAsync(point, CancellationToken.None));

        Assert.Equal(1L, await _postgres.ScalarAsync<long>($"SELECT count(*) FROM location_history WHERE point_id = '{point.PointId}'"));
    }

    [Fact]
    public async Task Shared_points_enter_history_and_not_shared_points_never_do()
    {
        var (store, dataSource) = await PrepareAsync();
        await using var _ = dataSource;
        var shared = Point(DateTimeOffset.UtcNow.AddMinutes(-40), shared: true);
        var notShared = Point(DateTimeOffset.UtcNow.AddMinutes(-41), shared: false);

        await store.StoreAsync(shared, CancellationToken.None);
        await store.StoreAsync(notShared, CancellationToken.None);

        Assert.True(await _postgres.ScalarAsync<bool>($"SELECT shared FROM location_history WHERE point_id = '{shared.PointId}'"));
        Assert.Equal(0L, await _postgres.ScalarAsync<long>($"SELECT count(*) FROM location_history WHERE point_id = '{notShared.PointId}'"));
    }

    [Fact]
    public async Task Rows_stored_before_the_flag_existed_become_shared_and_new_inserts_need_it()
    {
        var (_, dataSource) = await PrepareAsync();
        await using var _ = dataSource;

        // The default was dropped, so a raw insert that omits the flag must fail.
        await Assert.ThrowsAsync<PostgresException>(() => _postgres.ScalarAsync<int>(
            "INSERT INTO location_history (point_id, party_id, device_id, captured_at, received_at, latitude, longitude, accuracy_m, retention_expires_at) " +
            "VALUES (gen_random_uuid(), 'IND_x', 'd', now(), now(), 1, 1, 1, current_date) RETURNING 1"));
    }

    private async Task<(string PartyId, NpgsqlDataSource DataSource, PostgresLocationPointStore Store)> FreshPartyAsync()
    {
        var (store, dataSource) = await PrepareAsync();
        return ($"IND_{Guid.NewGuid()}", dataSource, store);
    }

    private Task<long> HistoryCountAsync(string party) =>
        _postgres.ScalarAsync<long>($"SELECT count(*) FROM location_history WHERE party_id = '{party}'");

    private Task<Guid> LatestPointAsync(string party) =>
        _postgres.ScalarAsync<Guid>($"SELECT point_id FROM latest_location WHERE party_id = '{party}'");

    [Fact]
    public async Task Shared_point_is_in_history_and_is_the_latest_position()
    {
        var (party, dataSource, store) = await FreshPartyAsync();
        await using var _ = dataSource;
        var point = Point(DateTimeOffset.UtcNow.AddMinutes(-5), party: party);

        await store.StoreAsync(point, CancellationToken.None);

        Assert.Equal(1L, await HistoryCountAsync(party));
        Assert.Equal(point.PointId, await LatestPointAsync(party));
        Assert.True(await _postgres.ScalarAsync<bool>($"SELECT shared FROM latest_location WHERE party_id = '{party}'"));
    }

    [Fact]
    public async Task Not_shared_point_is_only_the_latest_position()
    {
        var (party, dataSource, store) = await FreshPartyAsync();
        await using var _ = dataSource;
        var point = Point(DateTimeOffset.UtcNow.AddMinutes(-5), party: party, shared: false);

        Assert.Equal(StoreOutcome.Stored, await store.StoreAsync(point, CancellationToken.None));

        Assert.Equal(0L, await HistoryCountAsync(party));
        Assert.Equal(point.PointId, await LatestPointAsync(party));
        Assert.False(await _postgres.ScalarAsync<bool>($"SELECT shared FROM latest_location WHERE party_id = '{party}'"));
    }

    [Fact]
    public async Task Newer_not_shared_point_replaces_the_latest_position_and_there_is_one_row()
    {
        var (party, dataSource, store) = await FreshPartyAsync();
        await using var _ = dataSource;
        var older = Point(DateTimeOffset.UtcNow.AddMinutes(-10), party: party, shared: false);
        var newer = Point(DateTimeOffset.UtcNow.AddMinutes(-5), party: party, shared: false);

        await store.StoreAsync(older, CancellationToken.None);
        Assert.Equal(StoreOutcome.Stored, await store.StoreAsync(newer, CancellationToken.None));

        Assert.Equal(newer.PointId, await LatestPointAsync(party));
        Assert.Equal(1L, await _postgres.ScalarAsync<long>($"SELECT count(*) FROM latest_location WHERE party_id = '{party}'"));
    }

    [Fact]
    public async Task Older_not_shared_point_is_superseded_and_changes_nothing()
    {
        var (party, dataSource, store) = await FreshPartyAsync();
        await using var _ = dataSource;
        var newer = Point(DateTimeOffset.UtcNow.AddMinutes(-5), party: party, shared: false);
        var older = Point(DateTimeOffset.UtcNow.AddMinutes(-10), party: party, shared: false);

        await store.StoreAsync(newer, CancellationToken.None);

        Assert.Equal(StoreOutcome.Superseded, await store.StoreAsync(older, CancellationToken.None));
        Assert.Equal(newer.PointId, await LatestPointAsync(party));
    }

    [Fact]
    public async Task Retrying_the_latest_not_shared_point_is_a_duplicate_not_superseded()
    {
        var (party, dataSource, store) = await FreshPartyAsync();
        await using var _ = dataSource;
        // Sub-microsecond ticks must not break the comparison with what PostgreSQL stored.
        var point = Point(DateTimeOffset.UtcNow.AddMinutes(-5).AddTicks(7), party: party, shared: false);

        await store.StoreAsync(point, CancellationToken.None);

        Assert.Equal(StoreOutcome.Duplicate, await store.StoreAsync(point, CancellationToken.None));
    }

    [Fact]
    public async Task Older_shared_point_still_enters_history_but_does_not_replace_a_newer_latest_position()
    {
        var (party, dataSource, store) = await FreshPartyAsync();
        await using var _ = dataSource;
        var newest = Point(DateTimeOffset.UtcNow.AddMinutes(-5), party: party, shared: false);
        var olderShared = Point(DateTimeOffset.UtcNow.AddMinutes(-30), party: party, shared: true);

        await store.StoreAsync(newest, CancellationToken.None);

        Assert.Equal(StoreOutcome.Stored, await store.StoreAsync(olderShared, CancellationToken.None));
        Assert.Equal(1L, await HistoryCountAsync(party));
        Assert.Equal(newest.PointId, await LatestPointAsync(party));
    }

    [Fact]
    public async Task Concurrent_points_leave_the_newest_as_latest()
    {
        var (party, dataSource, store) = await FreshPartyAsync();
        await using var _ = dataSource;
        var now = DateTimeOffset.UtcNow.AddMinutes(-20);
        var points = Enumerable.Range(0, 20).Select(i => Point(now.AddSeconds(i), party: party, shared: i % 2 == 0)).ToList();

        await Task.WhenAll(points.OrderBy(_ => Guid.NewGuid()).Select(p => store.StoreAsync(p, CancellationToken.None)));

        Assert.Equal(points[^1].PointId, await LatestPointAsync(party));
        Assert.Equal(10L, await HistoryCountAsync(party));
    }

    [Fact]
    public async Task Failed_history_insert_does_not_leave_a_latest_position()
    {
        var (party, dataSource, store) = await FreshPartyAsync();
        await using var _ = dataSource;

        // No partition exists 60 days back, so the history insert throws and the transaction rolls back.
        await Assert.ThrowsAsync<PostgresException>(() =>
            store.StoreAsync(Point(DateTimeOffset.UtcNow.AddDays(-60), party: party), CancellationToken.None));

        Assert.Equal(0L, await _postgres.ScalarAsync<long>($"SELECT count(*) FROM latest_location WHERE party_id = '{party}'"));
    }

    [Fact]
    public async Task Same_point_id_for_another_party_is_a_new_row()
    {
        var (store, dataSource) = await PrepareAsync();
        await using var _ = dataSource;
        var at = DateTimeOffset.UtcNow.AddMinutes(-30);
        var id = Guid.NewGuid();

        Assert.Equal(StoreOutcome.Stored, await store.StoreAsync(Point(at, id, "IND_a"), CancellationToken.None));
        Assert.Equal(StoreOutcome.Stored, await store.StoreAsync(Point(at, id, "IND_b"), CancellationToken.None));
    }

    [Fact]
    public async Task Point_captured_fourteen_days_ago_is_stored_in_its_own_partition()
    {
        var (store, dataSource) = await PrepareAsync();
        await using var _ = dataSource;
        var point = Point(DateTimeOffset.UtcNow.AddDays(-13));

        Assert.Equal(StoreOutcome.Stored, await store.StoreAsync(point, CancellationToken.None));

        var partition = await _postgres.ScalarAsync<string>(
            $"SELECT tableoid::regclass::text FROM location_history WHERE point_id = '{point.PointId}'");
        Assert.Equal($"location_history_p{point.CapturedAt.UtcDateTime:yyyyMMdd}", partition);
    }

    [Fact]
    public async Task Point_in_a_day_without_a_partition_fails_loudly()
    {
        var (store, dataSource) = await PrepareAsync();
        await using var _ = dataSource;

        await Assert.ThrowsAsync<PostgresException>(() =>
            store.StoreAsync(Point(DateTimeOffset.UtcNow.AddDays(-60)), CancellationToken.None));
    }
}

public class PostgresEndpointTests : IClassFixture<PostgresFixture>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly PostgresFixture _postgres;

    public PostgresEndpointTests(PostgresFixture postgres)
    {
        _postgres = postgres;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:LocationDb", postgres.ConnectionString);
            builder.UseSetting("Database:RunMigrationsOnStartup", "true");
        });
    }

    [Fact]
    public async Task Endpoint_persists_points_and_reports_a_retry_as_duplicate()
    {
        var client = _factory.CreateClient();
        var pointId = Guid.NewGuid();
        var body = new
        {
            points = new[]
            {
                new
                {
                    pointId,
                    capturedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                    utcOffsetMinutes = 330,
                    latitude = 28.6139,
                    longitude = 77.2090,
                    accuracy = 12.5,
                    speed = 1.4,
                    course = 270,
                    shared = true,
                },
            },
            device = new { id = "device-1", platform = "ios" },
        };

        async Task<JsonElement> Send()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/locationtracking/points") { Content = JsonContent.Create(body) };
            request.Headers.Add(DevelopmentHeaderCallerContext.PartyIdHeader, "IND_335dc59b-2b6a-4fa9-976f-d0c2b3d0647f");
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var first = await Send();
        var second = await Send();

        Assert.Equal("Stored", first.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal("Duplicate", second.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal(1L, await _postgres.ScalarAsync<long>($"SELECT count(*) FROM location_history WHERE point_id = '{pointId}'"));
        Assert.IsType<PostgresLocationPointStore>(_factory.Services.GetRequiredService<ILocationPointStore>());
    }
}
