using Npgsql;
using NpgsqlTypes;

namespace ISOS.LocationService.Policy;

public sealed class PostgresPolicyStore(NpgsqlDataSource dataSource) : IPolicyStore
{
    public async Task<ClientPolicy?> GetClientPolicyAsync(string clientId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT client_id, sharing_enabled, type_always, type_schedule, type_active_travel, type_event_window, " +
            "allow_user_view_history, retention_days, event_window_minutes, visible_from, version " +
            "FROM client_location_policy WHERE client_id = @client_id");
        command.Parameters.AddWithValue("client_id", clientId);
        await using var r = await command.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken)
            ? new ClientPolicy(
                r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4), r.GetBoolean(5),
                r.GetBoolean(6), r.GetInt32(7), r.GetInt32(8), NullableTime(r, 9), r.GetInt32(10))
            : null;
    }

    public async Task<UserPolicyState> GetUserStateAsync(
        string partyId, string clientId, DateTimeOffset eventWindowsSince, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        ConsentRecord? consent = null;
        await using (var command = Command(connection, null,
            "SELECT status, disclosure_version, granted_at, revoked_at, visible_from FROM user_location_consent " +
            "WHERE party_id = @party_id AND client_id = @client_id", partyId, clientId))
        await using (var r = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await r.ReadAsync(cancellationToken))
            {
                consent = new ConsentRecord(r.GetString(0), r.GetString(1), NullableTime(r, 2), NullableTime(r, 3), NullableTime(r, 4));
            }
        }

        string? primaryType = null, homeTimezone = null;
        await using (var command = Command(connection, null,
            "SELECT primary_type, home_timezone FROM user_sharing_preference WHERE party_id = @party_id AND client_id = @client_id",
            partyId, clientId))
        await using (var r = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await r.ReadAsync(cancellationToken))
            {
                primaryType = r.GetString(0);
                homeTimezone = r.IsDBNull(1) ? null : r.GetString(1);
            }
        }

        var windows = new List<ScheduleWindow>();
        await using (var command = Command(connection, null,
            "SELECT day_of_week, start_time, end_time FROM schedule_window WHERE party_id = @party_id AND client_id = @client_id " +
            "ORDER BY day_of_week, start_time", partyId, clientId))
        await using (var r = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await r.ReadAsync(cancellationToken))
            {
                windows.Add(new ScheduleWindow(r.GetInt16(0), r.GetFieldValue<TimeOnly>(1), r.GetFieldValue<TimeOnly>(2)));
            }
        }

        var eventWindows = new List<EventWindow>();
        await using (var command = Command(connection, null,
            "SELECT trigger_type, source_ref, starts_at, expires_at FROM event_window_activation " +
            "WHERE party_id = @party_id AND client_id = @client_id AND expires_at > @since ORDER BY starts_at", partyId, clientId))
        {
            command.Parameters.Add(Timestamp("since", eventWindowsSince));
            await using var r = await command.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
            {
                eventWindows.Add(new EventWindow(
                    r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetFieldValue<DateTimeOffset>(2), r.GetFieldValue<DateTimeOffset>(3)));
            }
        }

        return new UserPolicyState(
            consent, primaryType is null ? null : new SharingPreference(primaryType, homeTimezone, windows), eventWindows);
    }

    public async Task GrantConsentAsync(
        string partyId, string clientId, string disclosureVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The WHERE makes a retry of the same grant a no-op, so granted_at keeps the first time.
        await using var command = Command(null, null,
            "INSERT INTO user_location_consent (party_id, client_id, status, disclosure_version, granted_at) " +
            "VALUES (@party_id, @client_id, 'GRANTED', @version, @now) " +
            "ON CONFLICT (party_id, client_id) DO UPDATE SET status = 'GRANTED', disclosure_version = @version, granted_at = @now " +
            "WHERE user_location_consent.status <> 'GRANTED' OR user_location_consent.disclosure_version <> @version",
            partyId, clientId);
        command.Parameters.AddWithValue("version", disclosureVersion);
        command.Parameters.Add(Timestamp("now", now));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RevokeConsentAsync(string partyId, string clientId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var revoke = Command(connection, transaction,
            "UPDATE user_location_consent SET status = 'REVOKED', revoked_at = @now, visible_from = @now " +
            "WHERE party_id = @party_id AND client_id = @client_id AND status = 'GRANTED'", partyId, clientId))
        {
            revoke.Parameters.Add(Timestamp("now", now));
            if (await revoke.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                return;
            }
        }

        // Only a live-sharing position goes: a not-shared point belongs to AEC or Location Based Alerts.
        await using (var latest = Command(connection, transaction,
            "DELETE FROM latest_location WHERE party_id = @party_id AND shared", partyId, null))
        {
            await latest.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var sessions = Command(connection, transaction,
            "UPDATE device_session SET state = 'ENDED' WHERE party_id = @party_id AND client_id = @client_id AND state <> 'ENDED'",
            partyId, clientId))
        {
            await sessions.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SavePreferenceAsync(
        string partyId, string clientId, SharingPreference preference, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var upsert = Command(connection, transaction,
            "INSERT INTO user_sharing_preference (party_id, client_id, primary_type, home_timezone, updated_at) " +
            "VALUES (@party_id, @client_id, @primary_type, @home_timezone, @now) " +
            "ON CONFLICT (party_id, client_id) DO UPDATE SET primary_type = @primary_type, home_timezone = @home_timezone, updated_at = @now",
            partyId, clientId))
        {
            upsert.Parameters.AddWithValue("primary_type", preference.PrimaryType);
            upsert.Parameters.Add(new NpgsqlParameter("home_timezone", NpgsqlDbType.Text) { Value = (object?)preference.HomeTimezone ?? DBNull.Value });
            upsert.Parameters.Add(Timestamp("now", now));
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var delete = Command(connection, transaction,
            "DELETE FROM schedule_window WHERE party_id = @party_id AND client_id = @client_id", partyId, clientId))
        {
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var window in preference.Schedule)
        {
            await using var insert = Command(connection, transaction,
                "INSERT INTO schedule_window (party_id, client_id, day_of_week, start_time, end_time) " +
                "VALUES (@party_id, @client_id, @day, @start, @end)", partyId, clientId);
            insert.Parameters.Add(new NpgsqlParameter("day", NpgsqlDbType.Smallint) { Value = (short)window.DayOfWeek });
            insert.Parameters.Add(new NpgsqlParameter("start", NpgsqlDbType.Time) { Value = window.Start });
            insert.Parameters.Add(new NpgsqlParameter("end", NpgsqlDbType.Time) { Value = window.End });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpsertDeviceStateAsync(DeviceStateUpdate u, CancellationToken cancellationToken)
    {
        var state = u.OsPermission == OsPermissions.Always && u.PreciseLocation
            ? DeviceSessionStates.Active
            : DeviceSessionStates.SuspendedOsPermission;
        await using var command = Command(null, null,
            "INSERT INTO device_session (party_id, client_id, device_id, platform, state, os_permission, precise_location, " +
            "last_timezone, last_seen_at, lease_expires_at) " +
            "VALUES (@party_id, @client_id, @device_id, @platform, @state, @os_permission, @precise, @tz, @now, @lease) " +
            "ON CONFLICT (party_id, client_id, device_id) DO UPDATE SET platform = COALESCE(@platform, device_session.platform), " +
            "state = @state, os_permission = @os_permission, precise_location = @precise, " +
            "last_timezone = COALESCE(@tz, device_session.last_timezone), last_seen_at = @now, lease_expires_at = @lease",
            u.PartyId, u.ClientId);
        command.Parameters.AddWithValue("device_id", u.DeviceId);
        command.Parameters.Add(new NpgsqlParameter("platform", NpgsqlDbType.Text) { Value = (object?)u.Platform ?? DBNull.Value });
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("os_permission", u.OsPermission);
        command.Parameters.AddWithValue("precise", u.PreciseLocation);
        command.Parameters.Add(new NpgsqlParameter("tz", NpgsqlDbType.Text) { Value = (object?)u.Timezone ?? DBNull.Value });
        command.Parameters.Add(Timestamp("now", u.Now));
        command.Parameters.Add(Timestamp("lease", u.LeaseExpiresAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EndDeviceSessionAsync(string partyId, string clientId, string deviceId, CancellationToken cancellationToken)
    {
        await using var command = Command(null, null,
            "UPDATE device_session SET state = 'ENDED' WHERE party_id = @party_id AND client_id = @client_id AND device_id = @device_id",
            partyId, clientId);
        command.Parameters.AddWithValue("device_id", deviceId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddEventWindowAsync(string partyId, string clientId, EventWindow window, CancellationToken cancellationToken)
    {
        await using var command = Command(null, null,
            "INSERT INTO event_window_activation (party_id, client_id, trigger_type, source_ref, starts_at, expires_at) " +
            "VALUES (@party_id, @client_id, @trigger, @source_ref, @starts_at, @expires_at)", partyId, clientId);
        command.Parameters.AddWithValue("trigger", window.TriggerType);
        command.Parameters.Add(new NpgsqlParameter("source_ref", NpgsqlDbType.Text) { Value = (object?)window.SourceRef ?? DBNull.Value });
        command.Parameters.Add(Timestamp("starts_at", window.StartsAt));
        command.Parameters.Add(Timestamp("expires_at", window.ExpiresAt));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DateTimeOffset?> GetFenceAsync(string partyId, string? clientId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT max(fence) FROM (" +
            "  SELECT c.visible_from AS fence FROM user_location_consent c " +
            "  WHERE c.party_id = @party_id AND (@client_id IS NULL OR c.client_id = @client_id) " +
            "  UNION ALL " +
            "  SELECT p.visible_from FROM client_location_policy p WHERE p.client_id IN (" +
            "    SELECT c.client_id FROM user_location_consent c " +
            "    WHERE c.party_id = @party_id AND (@client_id IS NULL OR c.client_id = @client_id))" +
            ") fences");
        command.Parameters.AddWithValue("party_id", partyId);
        command.Parameters.Add(new NpgsqlParameter("client_id", NpgsqlDbType.Text) { Value = (object?)clientId ?? DBNull.Value });
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DateTime value ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)) : null;
    }

    // With a connection the command joins its transaction; without one it uses the data source's own connection.
    private NpgsqlCommand Command(
        NpgsqlConnection? connection, NpgsqlTransaction? transaction, string sql, string partyId, string? clientId)
    {
        var command = connection is null ? dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("party_id", partyId);
        if (clientId is not null)
        {
            command.Parameters.AddWithValue("client_id", clientId);
        }

        return command;
    }

    private static NpgsqlParameter Timestamp(string name, DateTimeOffset value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value.UtcDateTime };

    private static DateTimeOffset? NullableTime(NpgsqlDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetFieldValue<DateTimeOffset>(ordinal);
}
