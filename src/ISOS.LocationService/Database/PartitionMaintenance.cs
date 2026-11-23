using Microsoft.Extensions.Options;
using Npgsql;
using ISOS.LocationService.Ingestion;

namespace ISOS.LocationService.Database;

/// <summary>Creates the daily history partitions an insert needs. Inserting into a day with no partition fails.</summary>
public sealed class PartitionMaintenance(NpgsqlDataSource dataSource, IOptions<IngestionOptions> options, TimeProvider clock)
{
    private const int DaysAhead = 7;

    public async Task<int> EnsureAsync(CancellationToken cancellationToken)
    {
        // Back to the oldest capture time the validator accepts, forward for clock skew and early creation.
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var first = today.AddDays(-(options.Value.RetentionDays + 1));
        var last = today.AddDays(DaysAhead);

        await using var command = dataSource.CreateCommand("SELECT ensure_location_history_partitions(@first, @last)");
        command.Parameters.AddWithValue("first", first);
        command.Parameters.AddWithValue("last", last);
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}

/// <summary>Keeps tomorrow's partition ready. Purging expired partitions is separate work (purge job).</summary>
public sealed class PartitionMaintenanceService(PartitionMaintenance maintenance, ILogger<PartitionMaintenanceService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                var created = await maintenance.EnsureAsync(stoppingToken);
                if (created > 0)
                {
                    logger.LogInformation("Created {Count} history partitions", created);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Partition maintenance failed; will retry");
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
