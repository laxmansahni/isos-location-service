using ISOS.LocationService.Database;
using ISOS.LocationService.Ingestion;
using ISOS.LocationService.Policy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ISOS.LocationService.Tests;

public class PostgresPolicyTests : IClassFixture<PostgresFixture>
{
    private const string Client = "client-1";
    private readonly PostgresFixture _postgres;

    public PostgresPolicyTests(PostgresFixture postgres) => _postgres = postgres;

    private static string NewParty() => $"IND_{Guid.NewGuid()}";

    private async Task<(PostgresPolicyStore Policy, PostgresLocationPointStore Points, NpgsqlDataSource DataSource)> PrepareAsync()
    {
        DatabaseMigrator.Migrate(_postgres.ConnectionString, NullLogger.Instance);
        var dataSource = NpgsqlDataSource.Create(_postgres.ConnectionString);
        await new PartitionMaintenance(dataSource, Options.Create(new IngestionOptions()), TimeProvider.System)
            .EnsureAsync(CancellationToken.None);
        return (new PostgresPolicyStore(dataSource), new PostgresLocationPointStore(dataSource), dataSource);
    }

    private static Task<UserPolicyState> StateAsync(PostgresPolicyStore store, string party, string client = Client) =>
        store.GetUserStateAsync(party, client, DateTimeOffset.UtcNow.AddDays(-30), CancellationToken.None);

    private static LocationPoint Point(string party, DateTimeOffset capturedAt, bool shared) => new(
        Guid.NewGuid(), party, Client, "device-1", capturedAt, 0, capturedAt, 51.5, -0.12, 10, null, null, null,
        QualityFlags.None, shared, DateOnly.FromDateTime(capturedAt.UtcDateTime).AddDays(14));

    [Fact]
    public async Task A_person_with_no_records_has_an_empty_state_and_a_client_without_a_row_has_no_policy()
    {
        var (store, _, _) = await PrepareAsync();

        var state = await StateAsync(store, NewParty());

        Assert.Null(state.Consent);
        Assert.Null(state.Preference);
        Assert.Empty(state.EventWindows);
        Assert.Null(await store.GetClientPolicyAsync("nobody", CancellationToken.None));
    }

    [Fact]
    public async Task Client_policy_is_read_back()
    {
        var (store, _, dataSource) = await PrepareAsync();
        var client = $"client-{Guid.NewGuid()}";
        await using (var command = dataSource.CreateCommand(
            $"INSERT INTO client_location_policy (client_id, sharing_enabled, type_schedule, retention_days, version) VALUES ('{client}', true, false, 30, 3)"))
        {
            await command.ExecuteNonQueryAsync();
        }

        var policy = await store.GetClientPolicyAsync(client, CancellationToken.None);

        Assert.NotNull(policy);
        Assert.True(policy.SharingEnabled);
        Assert.True(policy.TypeAlways);
        Assert.False(policy.TypeSchedule);
        Assert.Equal(30, policy.RetentionDays);
        Assert.Equal(60, policy.EventWindowMinutes);
        Assert.Equal(3, policy.Version);
        Assert.Null(policy.VisibleFrom);
    }

    [Fact]
    public async Task Granting_twice_keeps_the_first_time_and_a_new_disclosure_version_counts_as_new_consent()
    {
        var (store, _, _) = await PrepareAsync();
        var party = NewParty();
        var first = DateTimeOffset.UtcNow.AddMinutes(-10);

        await store.GrantConsentAsync(party, Client, "1", first, CancellationToken.None);
        await store.GrantConsentAsync(party, Client, "1", first.AddMinutes(5), CancellationToken.None);
        var same = (await StateAsync(store, party)).Consent!;
        await store.GrantConsentAsync(party, Client, "2", first.AddMinutes(8), CancellationToken.None);
        var newer = (await StateAsync(store, party)).Consent!;

        Assert.Equal(ConsentStatuses.Granted, same.Status);
        Assert.Equal(first, same.GrantedAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal("2", newer.DisclosureVersion);
        Assert.Equal(first.AddMinutes(8), newer.GrantedAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Revoking_sets_the_fence_deletes_the_shared_latest_ends_sessions_and_keeps_history_rows()
    {
        var (store, points, dataSource) = await PrepareAsync();
        var party = NewParty();
        var now = DateTimeOffset.UtcNow;
        await store.GrantConsentAsync(party, Client, "1", now.AddHours(-1), CancellationToken.None);
        await points.StoreAsync(Point(party, now.AddMinutes(-5), shared: true), CancellationToken.None);
        await store.UpsertDeviceStateAsync(
            new DeviceStateUpdate(party, Client, "phone-1", "ios", "ALWAYS", true, "UTC", now, now.AddHours(48)), CancellationToken.None);
        var otherClientDevice = new DeviceStateUpdate(party, "client-2", "phone-1", "ios", "ALWAYS", true, null, now, now.AddHours(48));
        await store.UpsertDeviceStateAsync(otherClientDevice, CancellationToken.None);

        await store.RevokeConsentAsync(party, Client, now, CancellationToken.None);

        var consent = (await StateAsync(store, party)).Consent!;
        Assert.Equal(ConsentStatuses.Revoked, consent.Status);
        Assert.Equal(now, consent.RevokedAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(now, consent.VisibleFrom!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(0L, await _postgres.ScalarAsync<long>($"SELECT count(*) FROM latest_location WHERE party_id = '{party}'"));
        Assert.Equal(1L, await _postgres.ScalarAsync<long>($"SELECT count(*) FROM location_history WHERE party_id = '{party}'"));
        Assert.Equal("ENDED", await _postgres.ScalarAsync<string>($"SELECT state FROM device_session WHERE party_id = '{party}' AND client_id = '{Client}'"));
        Assert.Equal("ACTIVE", await _postgres.ScalarAsync<string>($"SELECT state FROM device_session WHERE party_id = '{party}' AND client_id = 'client-2'"));
        await using var _ = dataSource;
    }

    [Fact]
    public async Task Revoking_keeps_a_not_shared_latest_position_and_does_nothing_when_consent_is_not_granted()
    {
        var (store, points, _) = await PrepareAsync();
        var party = NewParty();
        var now = DateTimeOffset.UtcNow;
        await store.GrantConsentAsync(party, Client, "1", now.AddHours(-1), CancellationToken.None);
        await points.StoreAsync(Point(party, now.AddMinutes(-5), shared: false), CancellationToken.None);

        await store.RevokeConsentAsync(party, Client, now, CancellationToken.None);
        await store.RevokeConsentAsync(party, Client, now.AddHours(1), CancellationToken.None);
        await store.RevokeConsentAsync(NewParty(), Client, now, CancellationToken.None);

        var consent = (await StateAsync(store, party)).Consent!;
        Assert.Equal(1L, await _postgres.ScalarAsync<long>($"SELECT count(*) FROM latest_location WHERE party_id = '{party}'"));
        Assert.Equal(now, consent.VisibleFrom!.Value, TimeSpan.FromMilliseconds(1)); // the second revoke did not move it
    }

    [Fact]
    public async Task Preference_round_trips_including_a_window_across_midnight_and_is_replaced_whole()
    {
        var (store, _, _) = await PrepareAsync();
        var party = NewParty();
        var now = DateTimeOffset.UtcNow;
        var night = new ScheduleWindow(5, new TimeOnly(22, 0), new TimeOnly(6, 30));
        var morning = new ScheduleWindow(1, new TimeOnly(9, 0), new TimeOnly(12, 15));

        await store.SavePreferenceAsync(party, Client, new SharingPreference(SharingTypes.Schedule, "Europe/London", [night, morning]), now, CancellationToken.None);
        var saved = (await StateAsync(store, party)).Preference!;
        await store.SavePreferenceAsync(party, Client, new SharingPreference(SharingTypes.Always, null, [morning]), now, CancellationToken.None);
        var replaced = (await StateAsync(store, party)).Preference!;

        Assert.Equal(SharingTypes.Schedule, saved.PrimaryType);
        Assert.Equal("Europe/London", saved.HomeTimezone);
        Assert.Equal([morning, night], saved.Schedule); // ordered by day
        Assert.Equal(SharingTypes.Always, replaced.PrimaryType);
        Assert.Null(replaced.HomeTimezone);
        Assert.Equal([morning], replaced.Schedule);
    }

    [Fact]
    public async Task Preferences_of_other_people_and_clients_are_separate()
    {
        var (store, _, _) = await PrepareAsync();
        var party = NewParty();
        var now = DateTimeOffset.UtcNow;
        await store.SavePreferenceAsync(party, Client, new SharingPreference(SharingTypes.Always, null, []), now, CancellationToken.None);

        Assert.Null((await StateAsync(store, party, "client-2")).Preference);
        Assert.Null((await StateAsync(store, NewParty())).Preference);
    }

    [Fact]
    public async Task Event_windows_older_than_the_cut_off_are_left_out()
    {
        var (store, _, _) = await PrepareAsync();
        var party = NewParty();
        var now = DateTimeOffset.UtcNow;
        await store.AddEventWindowAsync(party, Client, new EventWindow(EventTriggers.Aec, "old", now.AddDays(-40), now.AddDays(-40).AddHours(1)), CancellationToken.None);
        await store.AddEventWindowAsync(party, Client, new EventWindow(EventTriggers.Aec, "recent", now.AddHours(-3), now.AddHours(-2)), CancellationToken.None);
        await store.AddEventWindowAsync(party, Client, new EventWindow(EventTriggers.SpecialAdvisory, null, now.AddMinutes(-5), now.AddMinutes(55)), CancellationToken.None);

        var windows = (await StateAsync(store, party)).EventWindows;

        Assert.Equal(["recent", null], windows.Select(w => w.SourceRef));
        Assert.Equal(EventTriggers.SpecialAdvisory, windows[1].TriggerType);
    }

    [Fact]
    public async Task Device_state_is_upserted_and_logout_ends_only_that_device()
    {
        var (store, _, _) = await PrepareAsync();
        var party = NewParty();
        var now = DateTimeOffset.UtcNow;

        await store.UpsertDeviceStateAsync(new DeviceStateUpdate(party, Client, "phone-1", "ios", "ALWAYS", true, "Europe/London", now, now.AddHours(48)), CancellationToken.None);
        await store.UpsertDeviceStateAsync(new DeviceStateUpdate(party, Client, "tablet-1", null, "ALWAYS", true, null, now, now.AddHours(48)), CancellationToken.None);
        await store.UpsertDeviceStateAsync(new DeviceStateUpdate(party, Client, "phone-1", null, "WHEN_IN_USE", true, null, now.AddMinutes(1), now.AddHours(49)), CancellationToken.None);
        var suspended = await _postgres.ScalarAsync<string>($"SELECT state FROM device_session WHERE party_id = '{party}' AND device_id = 'phone-1'");
        var keptPlatform = await _postgres.ScalarAsync<string>($"SELECT platform || '/' || last_timezone FROM device_session WHERE party_id = '{party}' AND device_id = 'phone-1'");
        await store.EndDeviceSessionAsync(party, Client, "phone-1", CancellationToken.None);
        await store.EndDeviceSessionAsync(party, Client, "never-seen", CancellationToken.None);

        Assert.Equal("SUSPENDED_OS_PERMISSION", suspended);
        Assert.Equal("ios/Europe/London", keptPlatform); // a report without a platform or zone keeps the earlier values
        Assert.Equal("ENDED", await _postgres.ScalarAsync<string>($"SELECT state FROM device_session WHERE party_id = '{party}' AND device_id = 'phone-1'"));
        Assert.Equal("ACTIVE", await _postgres.ScalarAsync<string>($"SELECT state FROM device_session WHERE party_id = '{party}' AND device_id = 'tablet-1'"));
    }

    [Fact]
    public async Task Fence_is_the_latest_of_the_consent_and_client_fences_and_can_be_limited_to_a_client()
    {
        var (store, _, dataSource) = await PrepareAsync();
        var party = NewParty();
        var client = $"client-{Guid.NewGuid()}";
        var now = DateTimeOffset.UtcNow;
        await store.GrantConsentAsync(party, client, "1", now.AddHours(-5), CancellationToken.None);
        await store.GrantConsentAsync(party, "client-other", "1", now.AddHours(-5), CancellationToken.None);
        Assert.Null(await store.GetFenceAsync(party, null, CancellationToken.None));

        await store.RevokeConsentAsync(party, client, now.AddHours(-2), CancellationToken.None);
        await using (var command = dataSource.CreateCommand(
            $"INSERT INTO client_location_policy (client_id, sharing_enabled, visible_from) VALUES ('{client}', true, @fence)"))
        {
            command.Parameters.AddWithValue("fence", NpgsqlTypes.NpgsqlDbType.TimestampTz, now.AddHours(-1).UtcDateTime);
            await command.ExecuteNonQueryAsync();
        }

        var forClient = await store.GetFenceAsync(party, client, CancellationToken.None);
        var anyClient = await store.GetFenceAsync(party, null, CancellationToken.None);
        var otherClient = await store.GetFenceAsync(party, "client-other", CancellationToken.None);

        Assert.Equal(now.AddHours(-1), forClient!.Value, TimeSpan.FromMilliseconds(1)); // the client fence is later than the consent's
        Assert.Equal(forClient, anyClient);
        Assert.Null(otherClient);
        Assert.Null(await store.GetFenceAsync(NewParty(), null, CancellationToken.None));
    }

    [Fact]
    public async Task Database_rejects_invalid_values()
    {
        var (_, _, dataSource) = await PrepareAsync();

        async Task Run(string sql)
        {
            await using var command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<PostgresException>(() => Run(
            "INSERT INTO user_location_consent (party_id, client_id, status, disclosure_version) VALUES ('p', 'c', 'MAYBE', '1')"));
        await Assert.ThrowsAsync<PostgresException>(() => Run(
            "INSERT INTO schedule_window (party_id, client_id, day_of_week, start_time, end_time) VALUES ('p', 'c', 7, '09:00', '10:00')"));
        await Assert.ThrowsAsync<PostgresException>(() => Run(
            "INSERT INTO schedule_window (party_id, client_id, day_of_week, start_time, end_time) VALUES ('p', 'c', 1, '09:00', '09:00')"));
        await Assert.ThrowsAsync<PostgresException>(() => Run(
            "INSERT INTO event_window_activation (party_id, client_id, trigger_type, starts_at, expires_at) VALUES ('p', 'c', 'AEC', now(), now() - interval '1 hour')"));
    }
}
