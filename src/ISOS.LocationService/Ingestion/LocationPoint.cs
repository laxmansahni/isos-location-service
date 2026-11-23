namespace ISOS.LocationService.Ingestion;

[Flags]
public enum QualityFlags
{
    None = 0,
    MockLocation = 1,
}

/// <summary>An accepted point as persisted. Tenant and person come from the caller, never from the body.</summary>
public sealed record LocationPoint(
    Guid PointId,
    string PartyId,
    string? ClientId,
    string DeviceId,
    DateTimeOffset CapturedAt,
    int? UtcOffsetMinutes,
    DateTimeOffset ReceivedAt,
    double Latitude,
    double Longitude,
    double AccuracyMeters,
    double? SpeedMetersPerSecond,
    double? CourseDegrees,
    double? AltitudeMeters,
    QualityFlags Flags,
    bool Shared,
    DateOnly RetentionExpiresAt);
