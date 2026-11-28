using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace ISOS.LocationService.Purge;

/// <summary>What one run removed. Partial counts are also kept for a failed run.</summary>
public sealed record PurgeResult(
    Guid RunId,
    long ExpiredHistoryRows,
    long FencedHistoryRows,
    long ExpiredLatestRows,
    long FencedLatestRows,
    IReadOnlyList<string> DroppedPartitions);

/// <summary>
/// The purge job (LLD 5.3). One run, in this order:
/// <list type="number">
/// <item>Fenced data: points captured at or before a person's or a client's visibility fence (ADR-005).</item>
/// <item>Expired history: rows past <c>retention_expires_at</c> in every daily partition, then partitions that are
/// empty and too old to take inserts are dropped.</item>
/// <item>Expired latest positions.</item>
/// </list>
/// A row is kept through its expiry date and removed by the first run after it, so data may outlive its retention by
/// up to one run interval. Every run leaves a row in <c>purge_run</c> as evidence.
/// </summary>
public sealed partial class PurgeService(
    NpgsqlDataSource dataSource,
    IOptions<PurgeOptions> options,
    TimeProvider clock,
    ILogger<PurgeService> logger)
{
    /// <summary>Advisory lock id that keeps two runs from overlapping.</summary>
    public const long LockKey = 7_104_001_001;

    [GeneratedRegex(@"^location_history_p(\d{8})$")]
    private static partial Regex PartitionName();

    private sealed class Counters
    {
        public long ExpiredHistory, FencedHistory, ExpiredLatest, FencedLatest;
        public readonly List<string> Dropped = [];
    }

    /// <summary>Returns null when another run holds the lock and this one did nothing.</summary>
    public async Task<PurgeResult?> RunAsync(CancellationToken cancellationToken)
    {
        // The lock belongs to this connection. Pooling would keep the session alive after Dispose, so it is released
        // explicitly; a crashed process loses its connection and with it the lock.
        await using var lockConnection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var tryLock = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", lockConnection))
        {
            tryLock.Parameters.AddWithValue("key", LockKey);
            if (!(bool)(await tryLock.ExecuteScalarAsync(cancellationToken))!)
            {
                logger.LogWarning("Another purge run is in progress; nothing done");
                return null;
            }
        }

        try
        {
            return await RunLockedAsync(cancellationToken);
        }
        finally
        {
            try
            {
                await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", lockConnection);
                unlock.Parameters.AddWithValue("key", LockKey);
                await unlock.ExecuteScalarAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not release the purge lock");
            }
        }
    }

    private async Task<PurgeResult> RunLockedAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.BatchSize <= 0 || settings.RetentionDays <= 0)
        {
            throw new InvalidOperationException("Purge:BatchSize and the retention days must be positive.");
        }

        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var counters = new Counters();
        var runId = await StartRunAsync(now, today, cancellationToken);
        logger.LogInformation("Purge run {RunId} started for retention date {Today}", runId, today);

        try
        {
            await PurgeFencedAsync(now, counters, cancellationToken);
            await PurgeHistoryAsync(today, counters, cancellationToken);
            counters.ExpiredLatest = await DeleteInBatchesAsync(
                "latest_location", "retention_expires_at < @today", today, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Purge run {RunId} failed", runId);
            await FinishRunAsync(runId, "FAILED", counters, Describe(ex));
            throw;
        }

        await FinishRunAsync(runId, "SUCCEEDED", counters, null);
        var result = ToResult(runId, counters);
        logger.LogInformation(
            "Purge run {RunId} finished: expired history {ExpiredHistory}, fenced history {FencedHistory}, " +
            "expired latest {ExpiredLatest}, fenced latest {FencedLatest}, dropped partitions {Dropped}",
            runId, result.ExpiredHistoryRows, result.FencedHistoryRows, result.ExpiredLatestRows, result.FencedLatestRows,
            result.DroppedPartitions.Count);
        return result;
    }

    // ADR-005: data collected under a consent that has ended is deleted at the next run, not kept to retention.
    private async Task PurgeFencedAsync(DateTimeOffset now, Counters counters, CancellationToken cancellationToken)
    {
        // Everything stored before a fence expires within the retention window after it, so older fences have
        // nothing left to find and are not revisited.
        var cutoff = now - TimeSpan.FromDays(options.Value.RetentionDays + 1);

        var fences = new List<(string PartyId, string ClientId, DateTimeOffset Fence)>();
        await using (var command = dataSource.CreateCommand(
            "SELECT c.party_id, c.client_id, GREATEST(c.visible_from, p.visible_from) " +
            "FROM user_location_consent c LEFT JOIN client_location_policy p ON p.client_id = c.client_id " +
            "WHERE GREATEST(c.visible_from, p.visible_from) >= @cutoff"))
        {
            command.Parameters.Add(Timestamp("cutoff", cutoff));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                fences.Add((reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
            }
        }

        foreach (var (partyId, clientId, fence) in fences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            counters.FencedHistory += await DeleteForPersonAsync(
                "DELETE FROM location_history WHERE party_id = @party AND client_id = @client AND captured_at <= @fence",
                partyId, clientId, fence, cancellationToken);

            // A not-shared position belongs to AEC or Location Based Alerts, not to live sharing, so it stays.
            counters.FencedLatest += await DeleteForPersonAsync(
                "DELETE FROM latest_location WHERE party_id = @party AND client_id = @client AND shared AND captured_at <= @fence",
                partyId, clientId, fence, cancellationToken);
        }
    }

    private async Task PurgeHistoryAsync(DateOnly today, Counters counters, CancellationToken cancellationToken)
    {
        // Ingestion creates and writes partitions from this day on. Older ones take no inserts, so they are safe to drop.
        var firstWritableDay = today.AddDays(-(options.Value.RetentionDays + 1));

        foreach (var (name, day) in await ListPartitionsAsync(cancellationToken))
        {
            // Late uploads mean a partition can hold unexpired rows for a long time after most of it has expired, so
            // expired rows go row by row first. Dropping is only the last step for an old partition that is now empty.
            counters.ExpiredHistory += await DeleteInBatchesAsync(name, "retention_expires_at < @today", today, cancellationToken);

            if (day < firstWritableDay && await IsEmptyAsync(name, cancellationToken)
                && await TryDropAsync(name, cancellationToken))
            {
                counters.Dropped.Add(name.Trim('"'));
            }
        }
    }

    // Partition names are created by ensure_location_history_partitions as location_history_pYYYYMMDD. Anything that
    // does not match is left alone, which also makes the names safe to put in SQL.
    private async Task<List<(string QuotedName, DateOnly Day)>> ListPartitionsAsync(CancellationToken cancellationToken)
    {
        var partitions = new List<(string, DateOnly)>();
        await using var command = dataSource.CreateCommand(
            "SELECT c.relname FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid " +
            "WHERE i.inhparent = 'location_history'::regclass ORDER BY c.relname");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            if (PartitionName().Match(name) is { Success: true } match
                && DateOnly.TryParseExact(match.Groups[1].Value, "yyyyMMdd", out var day))
            {
                partitions.Add(($"\"{name}\"", day));
            }
        }

        return partitions;
    }

    private async Task<bool> IsEmptyAsync(string quotedTable, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"SELECT NOT EXISTS (SELECT 1 FROM {quotedTable})");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<bool> TryDropAsync(string quotedTable, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // Dropping a partition locks the parent table. Give up quickly rather than make queries and inserts wait.
            await using (var timeout = new NpgsqlCommand("SELECT set_config('lock_timeout', @ms, true)", connection, transaction))
            {
                timeout.Parameters.AddWithValue("ms", ((long)options.Value.PartitionLockTimeout.TotalMilliseconds).ToString());
                await timeout.ExecuteScalarAsync(cancellationToken);
            }

            await using (var drop = new NpgsqlCommand($"DROP TABLE {quotedTable}", connection, transaction))
            {
                await drop.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            logger.LogWarning("Could not lock partition {Partition} to drop it; will retry at the next run", quotedTable.Trim('"'));
            return false;
        }
    }

    // One statement per batch, so a big backlog is never a single long transaction. ctid picks the batch cheaply.
    private async Task<long> DeleteInBatchesAsync(
        string table, string condition, DateOnly today, CancellationToken cancellationToken)
    {
        var batch = options.Value.BatchSize;
        var sql = $"DELETE FROM {table} WHERE ctid = ANY (ARRAY(SELECT ctid FROM {table} WHERE {condition} LIMIT @batch))";
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.Add(new NpgsqlParameter("today", NpgsqlDbType.Date) { Value = today });
            command.Parameters.AddWithValue("batch", batch);
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            total += deleted;
            if (deleted < batch)
            {
                return total;
            }
        }
    }

    private async Task<long> DeleteForPersonAsync(
        string sql, string partyId, string clientId, DateTimeOffset fence, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("party", partyId);
        command.Parameters.AddWithValue("client", clientId);
        command.Parameters.Add(Timestamp("fence", fence));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<Guid> StartRunAsync(DateTimeOffset now, DateOnly today, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO purge_run (started_at, status, retention_cutoff) VALUES (@started, 'RUNNING', @today) RETURNING run_id");
        command.Parameters.Add(Timestamp("started", now));
        command.Parameters.Add(new NpgsqlParameter("today", NpgsqlDbType.Date) { Value = today });
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    // Written even when the run was cancelled or failed, so the evidence always shows how far it got.
    private async Task FinishRunAsync(Guid runId, string status, Counters counters, string? error)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE purge_run SET finished_at = @finished, status = @status, expired_history_rows = @eh, fenced_history_rows = @fh, " +
            "expired_latest_rows = @el, fenced_latest_rows = @fl, dropped_partitions = @dropped, error = @error WHERE run_id = @id");
        command.Parameters.Add(Timestamp("finished", clock.GetUtcNow()));
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("eh", counters.ExpiredHistory);
        command.Parameters.AddWithValue("fh", counters.FencedHistory);
        command.Parameters.AddWithValue("el", counters.ExpiredLatest);
        command.Parameters.AddWithValue("fl", counters.FencedLatest);
        command.Parameters.Add(new NpgsqlParameter("dropped", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = counters.Dropped.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text) { Value = (object?)error ?? DBNull.Value });
        command.Parameters.AddWithValue("id", runId);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static PurgeResult ToResult(Guid runId, Counters c) =>
        new(runId, c.ExpiredHistory, c.FencedHistory, c.ExpiredLatest, c.FencedLatest, c.Dropped.ToList());

    private static string Describe(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        return text.Length > 1000 ? text[..1000] : text;
    }

    private static NpgsqlParameter Timestamp(string name, DateTimeOffset value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value.UtcDateTime };
}
