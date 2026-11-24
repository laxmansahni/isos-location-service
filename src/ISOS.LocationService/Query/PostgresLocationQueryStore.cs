using System.Globalization;
using System.Text;
using ISOS.LocationService.Ingestion;
using Npgsql;
using NpgsqlTypes;

namespace ISOS.LocationService.Query;

public sealed class PostgresLocationQueryStore(NpgsqlDataSource dataSource) : ILocationQueryStore
{
    private const string Columns = PostgresLocationPointStore.Columns;

    public async Task<IReadOnlyList<LocationPoint>> GetLatestSharedAsync(LatestQuery query, CancellationToken cancellationToken)
    {
        var sql = new StringBuilder($"SELECT {Columns} FROM latest_location WHERE shared = true AND captured_at >= @not_older_than");
        if (query.Since is not null)
        {
            sql.Append(" AND captured_at > @since");
        }

        if (query.ClientId is not null)
        {
            sql.Append(" AND client_id = @client_id");
        }

        sql.Append(" ORDER BY captured_at DESC, party_id");

        await using var command = dataSource.CreateCommand(sql.ToString());
        command.Parameters.Add(Timestamp("not_older_than", query.NotOlderThan));
        if (query.Since is { } since)
        {
            command.Parameters.Add(Timestamp("since", since));
        }

        if (query.ClientId is not null)
        {
            command.Parameters.AddWithValue("client_id", query.ClientId);
        }

        return await ReadAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<LocationPoint>> GetHistoryAsync(HistoryQuery query, CancellationToken cancellationToken)
    {
        var (comparison, direction) = query.NewestFirst ? ("<", "DESC") : (">", "ASC");
        var sql = new StringBuilder(
            $"SELECT {Columns} FROM location_history WHERE party_id = @party_id AND shared = true AND captured_at >= @from");
        if (query.After is not null)
        {
            sql.Append($" AND (captured_at, point_id) {comparison} (@after_at, @after_id)");
        }

        sql.Append($" ORDER BY captured_at {direction}, point_id {direction} LIMIT @limit");

        await using var command = dataSource.CreateCommand(sql.ToString());
        command.Parameters.AddWithValue("party_id", query.PartyId);
        command.Parameters.Add(Timestamp("from", query.From));
        if (query.After is { } after)
        {
            command.Parameters.Add(Timestamp("after_at", after.At));
            command.Parameters.AddWithValue("after_id", after.PointId);
        }

        command.Parameters.AddWithValue("limit", query.Limit);
        return await ReadAsync(command, cancellationToken);
    }

    private static NpgsqlParameter Timestamp(string name, DateTimeOffset value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value.UtcDateTime };

    private static async Task<IReadOnlyList<LocationPoint>> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var points = new List<LocationPoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            points.Add(Map(reader));
        }

        return points;
    }

    // Column order is the order of PostgresLocationPointStore.Columns.
    private static LocationPoint Map(NpgsqlDataReader r) => new(
        PointId: r.GetGuid(0),
        PartyId: r.GetString(1),
        ClientId: r.IsDBNull(2) ? null : r.GetString(2),
        DeviceId: r.GetString(3),
        CapturedAt: new DateTimeOffset(r.GetDateTime(4), TimeSpan.Zero),
        UtcOffsetMinutes: r.IsDBNull(5) ? null : r.GetInt16(5),
        ReceivedAt: new DateTimeOffset(r.GetDateTime(6), TimeSpan.Zero),
        Latitude: r.GetDouble(7),
        Longitude: r.GetDouble(8),
        AccuracyMeters: Real(r, 9)!.Value,
        SpeedMetersPerSecond: Real(r, 10),
        CourseDegrees: Real(r, 11),
        AltitudeMeters: Real(r, 12),
        Flags: (QualityFlags)r.GetInt32(13),
        Shared: r.GetBoolean(14),
        RetentionExpiresAt: DateOnly.FromDateTime(r.GetDateTime(15)));

    // A real column holds a float: widening it directly would turn 1.4 into 1.399999976158142.
    private static double? Real(NpgsqlDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : double.Parse(r.GetFloat(ordinal).ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
