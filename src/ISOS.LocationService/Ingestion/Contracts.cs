using System.Text.Json.Serialization;

namespace ISOS.LocationService.Ingestion;

public sealed record SubmitPointsRequest(
    IReadOnlyList<PointDto>? Points,
    DeviceDto? Device);

/// <param name="PointId">Generated on the device. With the party and capture time it makes a retry idempotent.</param>
/// <param name="CapturedAt">When the device captured the point (not when it was uploaded).</param>
/// <param name="UtcOffsetMinutes">Local time offset from UTC at capture, in minutes (India is 330). Used for schedule rules.</param>
/// <param name="Latitude">Degrees, -90 to 90.</param>
/// <param name="Longitude">Degrees, -180 to 180.</param>
/// <param name="Accuracy">Horizontal accuracy in metres.</param>
/// <param name="Speed">Ground speed in metres per second. Omit when unknown.</param>
/// <param name="Course">Direction of travel in degrees clockwise from true north (0 = north, 90 = east), 0 to 360. Omit when unknown or stationary.</param>
/// <param name="Altitude">Metres above sea level. Optional.</param>
/// <param name="MockLocation">True when the OS reports a simulated location. Stored as a quality flag.</param>
/// <param name="Shared">Required. True when live location sharing is on for this point: it is added to the history
/// managers can see and becomes the latest position. False when the point was captured only for AEC or Location
/// Based Alerts ("not shared"): it is never added to history and only replaces the latest position when it is newer.</param>
public sealed record PointDto(
    Guid? PointId,
    DateTimeOffset? CapturedAt,
    int? UtcOffsetMinutes,
    double? Latitude,
    double? Longitude,
    double? Accuracy,
    double? Speed,
    double? Course,
    double? Altitude,
    bool? MockLocation,
    bool? Shared);

public sealed record DeviceDto(
    string? Id,
    string? Platform,
    string? AppVersion,
    string? Timezone);

public sealed record SubmitPointsResponse(
    int Stored,
    int Duplicates,
    int Superseded,
    int Rejected,
    IReadOnlyList<PointResult> Results);

public sealed record PointResult(
    Guid? PointId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] PointStatus Status,
    string? Reason);

/// <summary>
/// Stored: accepted. Duplicate: the same point was already stored. Superseded: a not-shared point that is not newer
/// than the latest position, so it was dropped. Rejected: invalid. The device may delete a point in every status.
/// </summary>
public enum PointStatus
{
    Stored,
    Duplicate,
    Superseded,
    Rejected,
}
