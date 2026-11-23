namespace ISOS.LocationService.Ingestion;

public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    public int MaxBatchSize { get; set; } = 100;

    /// <summary>How far a device clock may run ahead of the server before a point is rejected.</summary>
    public TimeSpan MaxFutureSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Points older than this are rejected. Matches the default retention (ADR-002).</summary>
    public int RetentionDays { get; set; } = 14;

    public double MaxAccuracyMeters { get; set; } = 10_000;
}
