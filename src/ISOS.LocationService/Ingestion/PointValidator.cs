using Microsoft.Extensions.Options;

namespace ISOS.LocationService.Ingestion;

public static class PointRejectionReasons
{
    public const string MissingField = "MISSING_FIELD";
    public const string InvalidCoordinates = "INVALID_COORDINATES";
    public const string InvalidAccuracy = "INVALID_ACCURACY";
    public const string InvalidSpeed = "INVALID_SPEED";
    public const string InvalidCourse = "INVALID_COURSE";
    public const string CapturedInFuture = "CAPTURED_IN_FUTURE";
    public const string CapturedTooOld = "CAPTURED_TOO_OLD";
}

/// <summary>Per-point validation (LLD 5.2 step 2). Returns a rejection reason, or null when the point is valid.</summary>
public sealed class PointValidator(IOptions<IngestionOptions> options, TimeProvider clock)
{
    private readonly IngestionOptions _options = options.Value;

    public string? Validate(PointDto point)
    {
        if (point.PointId is null || point.PointId == Guid.Empty
            || point.CapturedAt is null
            || point.Latitude is null
            || point.Longitude is null
            || point.Accuracy is null
            || point.Shared is null)
        {
            return PointRejectionReasons.MissingField;
        }

        if (!double.IsFinite(point.Latitude.Value) || !double.IsFinite(point.Longitude.Value)
            || point.Latitude is < -90 or > 90
            || point.Longitude is < -180 or > 180)
        {
            return PointRejectionReasons.InvalidCoordinates;
        }

        if (!double.IsFinite(point.Accuracy.Value) || point.Accuracy < 0 || point.Accuracy > _options.MaxAccuracyMeters)
        {
            return PointRejectionReasons.InvalidAccuracy;
        }

        if (point.Speed is { } speed && (!double.IsFinite(speed) || speed < 0))
        {
            return PointRejectionReasons.InvalidSpeed;
        }

        if (point.Course is { } course && (!double.IsFinite(course) || course < 0 || course > 360))
        {
            return PointRejectionReasons.InvalidCourse;
        }

        var now = clock.GetUtcNow();
        if (point.CapturedAt > now + _options.MaxFutureSkew)
        {
            return PointRejectionReasons.CapturedInFuture;
        }

        if (point.CapturedAt < now - TimeSpan.FromDays(_options.RetentionDays))
        {
            return PointRejectionReasons.CapturedTooOld;
        }

        return null;
    }
}
