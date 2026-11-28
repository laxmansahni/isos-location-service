namespace ISOS.LocationService.Purge;

public sealed class PurgeOptions
{
    public const string SectionName = "Purge";

    /// <summary>
    /// The ingestion retention window. Partitions inside it still take inserts, so they are never dropped. Must equal
    /// the API's setting: when <c>Ingestion:RetentionDays</c> is configured it is used, so one value serves both.
    /// </summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>Rows deleted per statement, so a large backlog never holds one long transaction.</summary>
    public int BatchSize { get; set; } = 10_000;

    /// <summary>
    /// How long dropping a partition may wait for a lock. A long running query holding it makes the drop give up and
    /// try again at the next run instead of queueing ingestion behind it.
    /// </summary>
    public TimeSpan PartitionLockTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
