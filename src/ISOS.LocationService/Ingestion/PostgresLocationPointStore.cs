using Npgsql;
using NpgsqlTypes;

namespace ISOS.LocationService.Ingestion;

public sealed class PostgresLocationPointStore(NpgsqlDataSource dataSource) : ILocationPointStore
{
    private const string Columns =
        "point_id, party_id, client_id, device_id, captured_at, utc_offset_min, received_at, " +
        "latitude, longitude, accuracy_m, speed_mps, course_deg, altitude_m, quality_flags, shared, retention_expires_at";

    private const string Values =
        "@point_id, @party_id, @client_id, @device_id, @captured_at, @utc_offset_min, @received_at, " +
        "@latitude, @longitude, @accuracy_m, @speed_mps, @course_deg, @altitude_m, @quality_flags, @shared, @retention_expires_at";

    private const string InsertHistorySql =
        $"INSERT INTO location_history ({Columns}) VALUES ({Values}) ON CONFLICT (party_id, captured_at, point_id) DO NOTHING";

    // The WHERE clause is what makes the newest capture time win, whatever order points arrive in.
    private const string UpsertLatestSql = $"""
        INSERT INTO latest_location ({Columns}) VALUES ({Values})
        ON CONFLICT (party_id) DO UPDATE SET
            client_id = EXCLUDED.client_id, device_id = EXCLUDED.device_id, point_id = EXCLUDED.point_id,
            captured_at = EXCLUDED.captured_at, utc_offset_min = EXCLUDED.utc_offset_min, received_at = EXCLUDED.received_at,
            latitude = EXCLUDED.latitude, longitude = EXCLUDED.longitude, accuracy_m = EXCLUDED.accuracy_m,
            speed_mps = EXCLUDED.speed_mps, course_deg = EXCLUDED.course_deg, altitude_m = EXCLUDED.altitude_m,
            quality_flags = EXCLUDED.quality_flags, shared = EXCLUDED.shared,
            retention_expires_at = EXCLUDED.retention_expires_at
        WHERE EXCLUDED.captured_at > latest_location.captured_at
        """;

    public async Task<StoreOutcome> StoreAsync(LocationPoint point, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (point.Shared)
        {
            if (!await ExecuteAsync(connection, transaction, InsertHistorySql, point, cancellationToken))
            {
                return StoreOutcome.Duplicate;
            }

            await ExecuteAsync(connection, transaction, UpsertLatestSql, point, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return StoreOutcome.Stored;
        }

        if (await ExecuteAsync(connection, transaction, UpsertLatestSql, point, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return StoreOutcome.Stored;
        }

        // Not newer. Either this exact point is already the latest (a retry) or something newer is.
        return await IsCurrentLatestAsync(connection, transaction, point, cancellationToken)
            ? StoreOutcome.Duplicate
            : StoreOutcome.Superseded;
    }

    private static async Task<bool> IsCurrentLatestAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, LocationPoint point, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT point_id, captured_at FROM latest_location WHERE party_id = @party_id", connection, transaction);
        command.Parameters.AddWithValue("party_id", point.PartyId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        // PostgreSQL keeps microseconds; compare at that precision.
        return reader.GetGuid(0) == point.PointId && reader.GetDateTime(1) == TruncateToMicroseconds(point.CapturedAt.UtcDateTime);
    }

    private static DateTime TruncateToMicroseconds(DateTime value) => new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);

    private static async Task<bool> ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, LocationPoint point, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddParameters(command.Parameters, point);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static void AddParameters(NpgsqlParameterCollection p, LocationPoint point)
    {
        p.AddWithValue("point_id", point.PointId);
        p.AddWithValue("party_id", point.PartyId);
        p.Add(new NpgsqlParameter("client_id", NpgsqlDbType.Text) { Value = (object?)point.ClientId ?? DBNull.Value });
        p.AddWithValue("device_id", point.DeviceId);
        p.Add(new NpgsqlParameter("captured_at", NpgsqlDbType.TimestampTz) { Value = point.CapturedAt.UtcDateTime });
        p.Add(new NpgsqlParameter("utc_offset_min", NpgsqlDbType.Smallint) { Value = point.UtcOffsetMinutes is { } o ? (short)o : DBNull.Value });
        p.Add(new NpgsqlParameter("received_at", NpgsqlDbType.TimestampTz) { Value = point.ReceivedAt.UtcDateTime });
        p.AddWithValue("latitude", point.Latitude);
        p.AddWithValue("longitude", point.Longitude);
        p.Add(new NpgsqlParameter("accuracy_m", NpgsqlDbType.Real) { Value = (float)point.AccuracyMeters });
        p.Add(new NpgsqlParameter("speed_mps", NpgsqlDbType.Real) { Value = point.SpeedMetersPerSecond is { } s ? (float)s : DBNull.Value });
        p.Add(new NpgsqlParameter("course_deg", NpgsqlDbType.Real) { Value = point.CourseDegrees is { } c ? (float)c : DBNull.Value });
        p.Add(new NpgsqlParameter("altitude_m", NpgsqlDbType.Real) { Value = point.AltitudeMeters is { } a ? (float)a : DBNull.Value });
        p.AddWithValue("quality_flags", (int)point.Flags);
        p.AddWithValue("shared", point.Shared);
        p.Add(new NpgsqlParameter("retention_expires_at", NpgsqlDbType.Date) { Value = point.RetentionExpiresAt });
    }
}
