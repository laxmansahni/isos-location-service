using ISOS.LocationService.Identity;
using ISOS.LocationService.Policy;
using Microsoft.Extensions.Options;

namespace ISOS.LocationService.Ingestion;

public sealed class IngestionService(
    PointValidator validator,
    PolicyEvaluator policyEvaluator,
    ILocationPointStore store,
    IOptions<IngestionOptions> options,
    TimeProvider clock)
{
    /// <summary>
    /// Validates and stores each point. With a <paramref name="policy"/>, live-sharing points (<c>shared = true</c>)
    /// are also checked against it one by one; not-shared points never are, because they are not live sharing
    /// (LLD 2.3). The policy may be null when the batch has no live points.
    /// </summary>
    public async Task<SubmitPointsResponse> SubmitAsync(
        CallerIdentity caller,
        DeviceDto device,
        IReadOnlyList<PointDto> points,
        PolicySnapshot? policy,
        CancellationToken cancellationToken)
    {
        var receivedAt = clock.GetUtcNow();
        // Per-client retention replaces this default once client policy exists (ADR-002).
        var retentionExpiresAt = DateOnly.FromDateTime(receivedAt.UtcDateTime).AddDays(options.Value.RetentionDays);
        var results = new List<PointResult>(points.Count);
        int stored = 0, duplicates = 0, superseded = 0, rejected = 0;

        foreach (var point in points)
        {
            var reason = validator.Validate(point);
            if (reason is not null)
            {
                rejected++;
                results.Add(new PointResult(point.PointId, PointStatus.Rejected, reason));
                continue;
            }

            if (point.Shared == true && policy is not null
                && policyEvaluator.Evaluate(policy, point.CapturedAt!.Value, point.UtcOffsetMinutes, device.Timezone) is { Allowed: false } denied)
            {
                rejected++;
                results.Add(new PointResult(point.PointId, PointStatus.Rejected, denied.Reason));
                continue;
            }

            var outcome = await store.StoreAsync(
                ToLocationPoint(caller, device, point, receivedAt, retentionExpiresAt), cancellationToken);
            switch (outcome)
            {
                case StoreOutcome.Stored:
                    stored++;
                    results.Add(new PointResult(point.PointId, PointStatus.Stored, null));
                    break;
                case StoreOutcome.Duplicate:
                    duplicates++;
                    results.Add(new PointResult(point.PointId, PointStatus.Duplicate, null));
                    break;
                default:
                    superseded++;
                    results.Add(new PointResult(point.PointId, PointStatus.Superseded, null));
                    break;
            }
        }

        return new SubmitPointsResponse(stored, duplicates, superseded, rejected, results);
    }

    private static LocationPoint ToLocationPoint(
        CallerIdentity caller, DeviceDto device, PointDto point, DateTimeOffset receivedAt, DateOnly retentionExpiresAt) =>
        new(
            PointId: point.PointId!.Value,
            PartyId: caller.PartyId,
            ClientId: caller.ClientId,
            DeviceId: device.Id!,
            CapturedAt: point.CapturedAt!.Value.ToUniversalTime(),
            UtcOffsetMinutes: point.UtcOffsetMinutes,
            ReceivedAt: receivedAt,
            Latitude: point.Latitude!.Value,
            Longitude: point.Longitude!.Value,
            AccuracyMeters: point.Accuracy!.Value,
            SpeedMetersPerSecond: point.Speed,
            CourseDegrees: point.Course,
            AltitudeMeters: point.Altitude,
            Flags: point.MockLocation == true ? QualityFlags.MockLocation : QualityFlags.None,
            Shared: point.Shared!.Value,
            RetentionExpiresAt: retentionExpiresAt);
}
