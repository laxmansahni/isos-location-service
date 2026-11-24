using Npgsql;
using NpgsqlTypes;

namespace ISOS.LocationService.Audit;

/// <summary>A manager's read of another person's location data. Never holds coordinates (LLD 4.4).</summary>
public sealed record AccessAuditEntry(
    DateTimeOffset OccurredAt,
    string ActorPartyId,
    string TargetPartyId,
    string DataType,
    string Range);

public interface IAccessAuditStore
{
    Task RecordAsync(AccessAuditEntry entry, CancellationToken cancellationToken);
}

public sealed class PostgresAccessAuditStore(NpgsqlDataSource dataSource) : IAccessAuditStore
{
    public async Task RecordAsync(AccessAuditEntry entry, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO access_audit (occurred_at, actor_party_id, target_party_id, data_type, range_requested) " +
            "VALUES (@occurred_at, @actor, @target, @data_type, @range)");
        command.Parameters.Add(new NpgsqlParameter("occurred_at", NpgsqlDbType.TimestampTz) { Value = entry.OccurredAt.UtcDateTime });
        command.Parameters.AddWithValue("actor", entry.ActorPartyId);
        command.Parameters.AddWithValue("target", entry.TargetPartyId);
        command.Parameters.AddWithValue("data_type", entry.DataType);
        command.Parameters.AddWithValue("range", entry.Range);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Development and test store. Not durable.</summary>
public sealed class InMemoryAccessAuditStore : IAccessAuditStore
{
    private readonly List<AccessAuditEntry> _entries = [];

    public IReadOnlyList<AccessAuditEntry> Entries
    {
        get { lock (_entries) { return [.. _entries]; } }
    }

    public Task RecordAsync(AccessAuditEntry entry, CancellationToken cancellationToken)
    {
        lock (_entries) { _entries.Add(entry); }
        return Task.CompletedTask;
    }
}
