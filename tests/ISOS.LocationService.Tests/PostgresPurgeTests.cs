using ISOS.LocationService.Database;
using ISOS.LocationService.Ingestion;
using ISOS.LocationService.Purge;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace ISOS.LocationService.Tests;

public class PostgresPurgeTests : IClassFixture<PostgresFixture>
{
    private const string Client = "client-1";
    private readonly PostgresFixture _postgres;

    public PostgresPurgeTests(PostgresFixture postgres) => _postgres = postgres;

    private static string NewParty() => $"IND_{Guid.NewGuid()}";

    private sealed class Context(NpgsqlDataSource dataSource, DateTimeOffset now, PurgeOptions options) : IAsyncDisposable
    {
        public NpgsqlDataSource DataSource => dataSource;
        public DateTimeOffset Now => now;
        public DateOnly Today => DateOnly.FromDateTime(now.UtcDateTime);

        public PurgeService Service { get; } = new(
            dataSource, Options.Create(options), new FakeTimeProvider(now), NullLogger<PurgeService>.Instance);

        public async Task ExecAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var command = dataSource.CreateCommand(sql);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            await command.ExecuteNonQueryAsync();
        }

        public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var command = dataSource.CreateCommand(sql);
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            return (T)(await command.ExecuteScalarAsync())!;
        }

        public Task InsertHistoryAsync(string party, string? client, DateTimeOffset capturedAt, DateOnly expires, bool shared = true) => ExecAsync(
            "INSERT INTO location_history (point_id, party_id, client_id, device_id, captured_at, received_at, latitude, longitude, " +
            "accuracy_m, quality_flags, shared, retention_expires_at) VALUES (@id, @party, @client, 'd', @at, @at, 1, 2, 5, 0, @shared, @expires)",
            ("id", Guid.NewGuid()), ("party", party), ("client", client), ("at", capturedAt.UtcDateTime), ("shared", shared), ("expires", expires));

        public Task InsertLatestAsync(string party, string? client, DateTimeOffset capturedAt, DateOnly expires, bool shared) => ExecAsync(
            "INSERT INTO latest_location (party_id, client_id, device_id, point_id, captured_at, received_at, latitude, longitude, " +
            "accuracy_m, quality_flags, shared, retention_expires_at) VALUES (@party, @client, 'd', @id, @at, @at, 1, 2, 5, 0, @shared, @expires)",
            ("party", party), ("client", client), ("id", Guid.NewGuid()), ("at", capturedAt.UtcDateTime), ("shared", shared), ("expires", expires));

        public Task<long> HistoryCountAsync(string party, string? client = null) => client is null
            ? ScalarAsync<long>("SELECT count(*) FROM location_history WHERE party_id = @party", ("party", party))
            : ScalarAsync<long>("SELECT count(*) FROM location_history WHERE party_id = @party AND client_id = @client", ("party", party), ("client", client));

        public Task<long> LatestCountAsync(string party) =>
            ScalarAsync<long>("SELECT count(*) FROM latest_location WHERE party_id = @party", ("party", party));

        public Task<bool> PartitionExistsAsync(int daysAgo) =>
            ScalarAsync<bool>("SELECT to_regclass(@name) IS NOT NULL", ("name", PartitionName(daysAgo)));

        public string PartitionName(int daysAgo) => $"location_history_p{Today.AddDays(-daysAgo):yyyyMMdd}";

        public Task RevokeConsentAsync(string party, string client, DateTimeOffset fence) => ExecAsync(
            "INSERT INTO user_location_consent (party_id, client_id, status, disclosure_version, granted_at, revoked_at, visible_from) " +
            "VALUES (@party, @client, 'REVOKED', '1', @granted, @fence, @fence)",
            ("party", party), ("client", client), ("granted", fence.AddDays(-1).UtcDateTime), ("fence", fence.UtcDateTime));

        public Task GrantConsentAsync(string party, string client) => ExecAsync(
            "INSERT INTO user_location_consent (party_id, client_id, status, disclosure_version, granted_at) VALUES (@party, @client, 'GRANTED', '1', @granted)",
            ("party", party), ("client", client), ("granted", now.AddDays(-5).UtcDateTime));

        public ValueTask DisposeAsync() => dataSource.DisposeAsync();
    }

    private async Task<Context> PrepareAsync(Action<PurgeOptions>? configure = null)
    {
        DatabaseMigrator.Migrate(_postgres.ConnectionString, NullLogger.Instance);
        var dataSource = NpgsqlDataSource.Create(_postgres.ConnectionString);
        var now = DateTimeOffset.UtcNow;
        var options = new PurgeOptions();
        configure?.Invoke(options);
        var context = new Context(dataSource, now, options);

        // Old days are created on purpose: the API would never accept points there, but a purge has to handle them.
        await context.ExecAsync(
            "SELECT ensure_location_history_partitions(@first, @last)",
            ("first", context.Today.AddDays(-90)), ("last", context.Today.AddDays(7)));
        return context;
    }

    private static async Task<PurgeResult> RunAsync(Context context)
    {
        var result = await context.Service.RunAsync(CancellationToken.None);
        return result ?? throw new InvalidOperationException("The run was skipped.");
    }

    // --- expired rows ---

    [Fact]
    public async Task Expired_history_rows_are_deleted_and_a_row_is_kept_through_its_expiry_date()
    {
        await using var context = await PrepareAsync();
        var party = NewParty();
        var captured = context.Now.AddDays(-3);
        await context.InsertHistoryAsync(party, Client, captured, context.Today.AddDays(-1)); // expired yesterday
        await context.InsertHistoryAsync(party, Client, captured, context.Today);              // expires today: kept
        await context.InsertHistoryAsync(party, Client, captured, context.Today.AddDays(5));

        var result = await RunAsync(context);

        Assert.Equal(2, await context.HistoryCountAsync(party));
        Assert.Equal(1, result.ExpiredHistoryRows);
    }

    [Fact]
    public async Task Expired_latest_rows_are_deleted_for_shared_and_not_shared_positions()
    {
        await using var context = await PrepareAsync();
        var (sharedExpired, notSharedExpired, kept) = (NewParty(), NewParty(), NewParty());
        await context.InsertLatestAsync(sharedExpired, Client, context.Now.AddDays(-20), context.Today.AddDays(-1), shared: true);
        await context.InsertLatestAsync(notSharedExpired, Client, context.Now.AddDays(-20), context.Today.AddDays(-1), shared: false);
        await context.InsertLatestAsync(kept, Client, context.Now.AddDays(-1), context.Today.AddDays(13), shared: false);

        var result = await RunAsync(context);

        Assert.Equal(0, await context.LatestCountAsync(sharedExpired));
        Assert.Equal(0, await context.LatestCountAsync(notSharedExpired));
        Assert.Equal(1, await context.LatestCountAsync(kept));
        Assert.Equal(2, result.ExpiredLatestRows);
    }

    [Fact]
    public async Task A_backlog_larger_than_the_batch_size_is_fully_deleted()
    {
        await using var context = await PrepareAsync(o => o.BatchSize = 2);
        var party = NewParty();
        for (var i = 0; i < 5; i++)
        {
            await context.InsertHistoryAsync(party, Client, context.Now.AddDays(-3).AddMinutes(i), context.Today.AddDays(-1));
        }

        var result = await RunAsync(context);

        Assert.Equal(0, await context.HistoryCountAsync(party));
        Assert.Equal(5, result.ExpiredHistoryRows);
    }

    // --- partitions ---

    [Fact]
    public async Task Old_partition_holding_only_expired_rows_is_dropped()
    {
        await using var context = await PrepareAsync();
        var party = NewParty();
        await context.InsertHistoryAsync(party, Client, context.Now.AddDays(-60), context.Today.AddDays(-45));

        var result = await RunAsync(context);

        Assert.False(await context.PartitionExistsAsync(60));
        Assert.Contains(context.PartitionName(60), result.DroppedPartitions);
        Assert.Equal(0, await context.HistoryCountAsync(party));
    }

    [Fact]
    public async Task Old_partition_with_a_late_upload_keeps_its_unexpired_row_and_loses_only_the_expired_ones()
    {
        await using var context = await PrepareAsync();
        var party = NewParty();
        var capturedLongAgo = context.Now.AddDays(-40);
        await context.InsertHistoryAsync(party, Client, capturedLongAgo, context.Today.AddDays(-20));
        await context.InsertHistoryAsync(party, Client, capturedLongAgo.AddMinutes(1), context.Today.AddDays(3)); // received late

        var result = await RunAsync(context);

        Assert.True(await context.PartitionExistsAsync(40));
        Assert.DoesNotContain(context.PartitionName(40), result.DroppedPartitions);
        Assert.Equal(1, await context.HistoryCountAsync(party));
    }

    [Fact]
    public async Task Partitions_that_still_take_inserts_are_never_dropped_even_when_empty()
    {
        await using var context = await PrepareAsync();

        var result = await RunAsync(context);

        // Ingestion creates partitions from today minus the retention days plus one, so that day and later stay.
        Assert.True(await context.PartitionExistsAsync(15));
        Assert.True(await context.PartitionExistsAsync(1));
        Assert.True(await context.PartitionExistsAsync(0));
        Assert.False(await context.PartitionExistsAsync(16));
        Assert.Contains(context.PartitionName(16), result.DroppedPartitions);
        Assert.DoesNotContain(context.PartitionName(15), result.DroppedPartitions);
    }

    [Fact]
    public async Task A_drop_that_cannot_get_its_lock_is_skipped_and_succeeds_at_the_next_run()
    {
        await using var context = await PrepareAsync(o => o.PartitionLockTimeout = TimeSpan.FromMilliseconds(300));
        var partition = context.PartitionName(70);
        await using var blocker = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand($"LOCK TABLE \"{partition}\" IN ACCESS SHARE MODE", blocker, transaction))
        {
            await hold.ExecuteNonQueryAsync();
        }

        var blocked = await RunAsync(context);
        await transaction.RollbackAsync();
        var released = await RunAsync(context);

        Assert.DoesNotContain(partition, blocked.DroppedPartitions);
        Assert.Contains(partition, released.DroppedPartitions);
        Assert.False(await context.PartitionExistsAsync(70));
    }

    [Fact]
    public async Task Tables_that_are_not_daily_history_partitions_are_left_alone()
    {
        await using var context = await PrepareAsync();
        await context.ExecAsync(
            "CREATE TABLE IF NOT EXISTS location_history_oddname PARTITION OF location_history " +
            "FOR VALUES FROM ('2001-01-01 00:00:00+00') TO ('2001-01-02 00:00:00+00')");
        var party = NewParty();
        await context.InsertHistoryAsync(party, Client, new DateTimeOffset(2001, 1, 1, 12, 0, 0, TimeSpan.Zero), context.Today.AddDays(-1));

        var result = await RunAsync(context);

        Assert.Equal(1, await context.HistoryCountAsync(party));
        Assert.DoesNotContain("location_history_oddname", result.DroppedPartitions);
        Assert.True(await context.ScalarAsync<bool>("SELECT to_regclass('location_history_oddname') IS NOT NULL"));
    }

    // --- fenced data ---

    [Fact]
    public async Task After_a_revoke_history_captured_up_to_the_fence_is_deleted_and_later_history_is_kept()
    {
        await using var context = await PrepareAsync();
        var party = NewParty();
        var otherParty = NewParty();
        var fence = context.Now.AddHours(-1);
        await context.RevokeConsentAsync(party, Client, fence);
        await context.GrantConsentAsync(otherParty, Client);
        var future = context.Today.AddDays(10);
        await context.InsertHistoryAsync(party, Client, fence.AddHours(-2), future); // before the fence
        await context.InsertHistoryAsync(party, Client, fence, future);              // at the fence: hidden too
        await context.InsertHistoryAsync(party, Client, fence.AddMinutes(30), future); // after a re-grant
        await context.InsertHistoryAsync(party, "client-2", fence.AddHours(-2), future); // another client
        await context.InsertHistoryAsync(otherParty, Client, fence.AddHours(-2), future); // another person

        var result = await RunAsync(context);

        Assert.Equal(1, await context.HistoryCountAsync(party, Client));
        Assert.Equal(1, await context.HistoryCountAsync(party, "client-2"));
        Assert.Equal(1, await context.HistoryCountAsync(otherParty));
        Assert.Equal(2, result.FencedHistoryRows);
        Assert.Equal(0, result.ExpiredHistoryRows);
    }

    [Fact]
    public async Task After_a_revoke_a_shared_latest_position_goes_but_a_not_shared_one_stays()
    {
        await using var context = await PrepareAsync();
        var (sharedParty, notSharedParty) = (NewParty(), NewParty());
        var fence = context.Now.AddHours(-1);
        await context.RevokeConsentAsync(sharedParty, Client, fence);
        await context.RevokeConsentAsync(notSharedParty, Client, fence);
        var future = context.Today.AddDays(10);
        await context.InsertLatestAsync(sharedParty, Client, fence.AddMinutes(-5), future, shared: true);
        await context.InsertLatestAsync(notSharedParty, Client, fence.AddMinutes(-5), future, shared: false);

        var result = await RunAsync(context);

        Assert.Equal(0, await context.LatestCountAsync(sharedParty));
        Assert.Equal(1, await context.LatestCountAsync(notSharedParty));
        Assert.Equal(1, result.FencedLatestRows);
    }

    [Fact]
    public async Task A_client_fence_removes_earlier_data_of_the_people_who_consented_under_it()
    {
        await using var context = await PrepareAsync();
        var fencedClient = $"client-{Guid.NewGuid()}";
        var openClient = $"client-{Guid.NewGuid()}";
        var (fencedParty, openParty) = (NewParty(), NewParty());
        var fence = context.Now.AddHours(-1);
        await context.ExecAsync(
            "INSERT INTO client_location_policy (client_id, sharing_enabled, visible_from) VALUES (@fenced, true, @fence), (@open, true, NULL)",
            ("fenced", fencedClient), ("open", openClient), ("fence", fence.UtcDateTime));
        await context.GrantConsentAsync(fencedParty, fencedClient);
        await context.GrantConsentAsync(openParty, openClient);
        var future = context.Today.AddDays(10);
        await context.InsertHistoryAsync(fencedParty, fencedClient, fence.AddHours(-3), future);
        await context.InsertHistoryAsync(fencedParty, fencedClient, fence.AddMinutes(10), future);
        await context.InsertHistoryAsync(openParty, openClient, fence.AddHours(-3), future);

        await RunAsync(context);

        Assert.Equal(1, await context.HistoryCountAsync(fencedParty));
        Assert.Equal(1, await context.HistoryCountAsync(openParty));
    }

    [Fact]
    public async Task A_fence_set_before_the_retention_window_is_not_revisited()
    {
        await using var context = await PrepareAsync();
        var party = NewParty();
        var oldFence = context.Now.AddDays(-30);
        await context.RevokeConsentAsync(party, Client, oldFence);
        await context.InsertHistoryAsync(party, Client, oldFence.AddDays(-1), context.Today.AddDays(10));

        var result = await RunAsync(context);

        // Anything captured before that fence expired long ago; this row only exists because the test forced it.
        Assert.Equal(1, await context.HistoryCountAsync(party));
        Assert.Equal(0, result.FencedHistoryRows);
    }

    // --- evidence, repeat runs and failures ---

    [Fact]
    public async Task A_run_is_recorded_as_evidence_and_a_repeat_run_deletes_nothing()
    {
        await using var context = await PrepareAsync();
        var party = NewParty();
        await context.InsertHistoryAsync(party, Client, context.Now.AddDays(-4), context.Today.AddDays(-2));
        await context.InsertHistoryAsync(NewParty(), Client, context.Now.AddDays(-65), context.Today.AddDays(-50));

        var first = await RunAsync(context);
        var second = await RunAsync(context);

        Assert.Equal(2, first.ExpiredHistoryRows);
        Assert.NotEmpty(first.DroppedPartitions);
        Assert.Equal(0, second.ExpiredHistoryRows + second.FencedHistoryRows + second.ExpiredLatestRows + second.FencedLatestRows);
        Assert.Empty(second.DroppedPartitions);
        var id = first.RunId;
        Assert.Equal("SUCCEEDED", await context.ScalarAsync<string>("SELECT status FROM purge_run WHERE run_id = @id", ("id", id)));
        Assert.True(await context.ScalarAsync<bool>("SELECT finished_at >= started_at FROM purge_run WHERE run_id = @id", ("id", id)));
        Assert.Equal(2L, await context.ScalarAsync<long>("SELECT expired_history_rows FROM purge_run WHERE run_id = @id", ("id", id)));
        Assert.Equal(first.DroppedPartitions.Count, await context.ScalarAsync<int>(
            "SELECT cardinality(dropped_partitions) FROM purge_run WHERE run_id = @id", ("id", id)));
        Assert.Equal(
            context.Today.ToString("yyyy-MM-dd"),
            await context.ScalarAsync<string>("SELECT retention_cutoff::text FROM purge_run WHERE run_id = @id", ("id", id)));
        Assert.True(await context.ScalarAsync<bool>("SELECT error IS NULL FROM purge_run WHERE run_id = @id", ("id", id)));
    }

    [Fact]
    public async Task A_failed_run_is_recorded_with_what_it_had_removed_so_far()
    {
        await using var context = await PrepareAsync();
        // Fences left by other tests would make the fence step touch latest_location, and fail, before history is purged.
        await context.ExecAsync("DELETE FROM user_location_consent");
        var party = NewParty();
        await context.InsertHistoryAsync(party, Client, context.Now.AddDays(-4), context.Today.AddDays(-2));
        await context.ExecAsync("ALTER TABLE latest_location RENAME TO latest_location_hidden");

        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => context.Service.RunAsync(CancellationToken.None));
        }
        finally
        {
            await context.ExecAsync("ALTER TABLE latest_location_hidden RENAME TO latest_location");
        }

        Assert.Equal(0, await context.HistoryCountAsync(party));
        const string latestFailed = "FROM purge_run WHERE status = 'FAILED' ORDER BY started_at DESC LIMIT 1";
        Assert.Equal(1L, await context.ScalarAsync<long>($"SELECT expired_history_rows {latestFailed}"));
        Assert.NotNull(await context.ScalarAsync<string>($"SELECT error {latestFailed}"));
        Assert.True(await context.ScalarAsync<bool>($"SELECT finished_at IS NOT NULL {latestFailed}"));
    }

    [Fact]
    public async Task A_second_run_while_one_is_in_progress_does_nothing()
    {
        await using var context = await PrepareAsync();
        var party = NewParty();
        await context.InsertHistoryAsync(party, Client, context.Now.AddDays(-4), context.Today.AddDays(-2));
        var runsBefore = await context.ScalarAsync<long>("SELECT count(*) FROM purge_run");

        await using (var holder = await context.DataSource.OpenConnectionAsync())
        {
            await using (var hold = new NpgsqlCommand($"SELECT pg_advisory_lock({PurgeService.LockKey})", holder))
            {
                await hold.ExecuteNonQueryAsync();
            }

            var skipped = await context.Service.RunAsync(CancellationToken.None);

            Assert.Null(skipped);
            Assert.Equal(1, await context.HistoryCountAsync(party));
            Assert.Equal(runsBefore, await context.ScalarAsync<long>("SELECT count(*) FROM purge_run"));
            await using var release = new NpgsqlCommand($"SELECT pg_advisory_unlock({PurgeService.LockKey})", holder);
            await release.ExecuteNonQueryAsync();
        }

        await RunAsync(context);
        Assert.Equal(0, await context.HistoryCountAsync(party));
    }

    [Fact]
    public async Task A_run_releases_its_lock_so_the_next_run_can_start()
    {
        await using var context = await PrepareAsync();

        var first = await context.Service.RunAsync(CancellationToken.None);
        var second = await context.Service.RunAsync(CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Invalid_settings_stop_the_run_before_anything_is_touched()
    {
        await using var context = await PrepareAsync(o => o.BatchSize = 0);
        var party = NewParty();
        await context.InsertHistoryAsync(party, Client, context.Now.AddDays(-4), context.Today.AddDays(-2));

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Service.RunAsync(CancellationToken.None));

        Assert.Equal(1, await context.HistoryCountAsync(party));
    }
}
