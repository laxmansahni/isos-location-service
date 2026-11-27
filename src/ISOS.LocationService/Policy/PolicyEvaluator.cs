using Microsoft.Extensions.Options;

namespace ISOS.LocationService.Policy;

/// <param name="Allowed">Whether live sharing is allowed.</param>
/// <param name="Reason">One of <see cref="PolicyDenials"/> when not allowed.</param>
public sealed record PolicyDecision(bool Allowed, string? Reason = null)
{
    public static readonly PolicyDecision Allow = new(true);

    public static PolicyDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// The effective policy of LLD 5.1, evaluated for the instant a point was captured:
/// client enabled, consent granted, captured after the fence, and (an active event window or the chosen type).
/// </summary>
public sealed class PolicyEvaluator(IOptions<PolicyOptions> options)
{
    private const int MaxOffsetMinutes = 14 * 60;

    /// <summary>True when the person has agreed to the current disclosure and has not revoked.</summary>
    public bool HasValidConsent(ConsentRecord? consent) =>
        consent is { Status: ConsentStatuses.Granted } && consent.DisclosureVersion == options.Value.DisclosureVersion;

    /// <summary>
    /// The checks that do not depend on when a point was captured. A denial here means live sharing is not allowed
    /// at all right now, so a batch of live points is refused as a whole.
    /// </summary>
    public PolicyDecision CheckEnabled(PolicySnapshot snapshot)
    {
        if (!snapshot.Client.SharingEnabled)
        {
            return PolicyDecision.Deny(PolicyDenials.SharingNotAllowed);
        }

        return HasValidConsent(snapshot.User.Consent)
            ? PolicyDecision.Allow
            : PolicyDecision.Deny(PolicyDenials.ConsentRequired);
    }

    /// <summary>
    /// Whether live sharing was allowed at <paramref name="capturedAt"/>. The point's own UTC offset wins over the zone for
    /// schedule windows; the device zone comes first in the zone resolution order (ADR-004).
    /// </summary>
    public PolicyDecision Evaluate(PolicySnapshot snapshot, DateTimeOffset capturedAt, int? utcOffsetMinutes, string? deviceTimezone)
    {
        var enabled = CheckEnabled(snapshot);
        if (!enabled.Allowed)
        {
            return enabled;
        }

        var consent = snapshot.User.Consent!;
        var fence = Later(snapshot.Client.VisibleFrom, consent.VisibleFrom);
        if (fence is { } f && capturedAt <= f)
        {
            return PolicyDecision.Deny(PolicyDenials.CapturedBeforeConsent);
        }

        if (consent.GrantedAt is { } grantedAt && capturedAt < grantedAt - options.Value.ConsentClockSkew)
        {
            return PolicyDecision.Deny(PolicyDenials.CapturedBeforeConsent);
        }

        // The event window is additive: it opens sharing even when the chosen type is closed.
        if (snapshot.Client.TypeEventWindow
            && snapshot.User.EventWindows.Any(w => w.StartsAt <= capturedAt && capturedAt < w.ExpiresAt))
        {
            return PolicyDecision.Allow;
        }

        var preference = snapshot.User.Preference;
        if (preference is null || !snapshot.Client.IsEnabled(preference.PrimaryType))
        {
            return PolicyDecision.Deny(PolicyDenials.NoActiveSharingType);
        }

        return preference.PrimaryType switch
        {
            SharingTypes.Always => PolicyDecision.Allow,
            SharingTypes.Schedule when InWindow(
                LocalTime(capturedAt, utcOffsetMinutes, ResolveZone(deviceTimezone, preference.HomeTimezone)), preference.Schedule)
                => PolicyDecision.Allow,
            // ACTIVE_TRAVEL needs the Trips feed, which does not exist yet, so it never allows.
            _ => PolicyDecision.Deny(PolicyDenials.OutsideSharingWindow),
        };
    }

    /// <summary>
    /// The next instant at which the answer may change (a schedule window opening or closing, an event window
    /// starting or ending), so the device knows when to ask again. Null when nothing is scheduled.
    /// </summary>
    public DateTimeOffset? NextEvaluationAt(PolicySnapshot snapshot, DateTimeOffset now, string? deviceTimezone)
    {
        var candidates = new List<DateTimeOffset>();

        if (snapshot.Client.TypeEventWindow)
        {
            foreach (var window in snapshot.User.EventWindows)
            {
                candidates.AddRange(new[] { window.StartsAt, window.ExpiresAt }.Where(t => t > now));
            }
        }

        if (snapshot.User.Preference is { PrimaryType: SharingTypes.Schedule } preference
            && snapshot.Client.IsEnabled(SharingTypes.Schedule))
        {
            candidates.AddRange(ScheduleBoundaries(now, ResolveZone(deviceTimezone, preference.HomeTimezone), preference.Schedule));
        }

        return candidates.Count == 0 ? null : candidates.Min();
    }

    /// <summary>Device zone, then the person's home zone, then the configured default (ADR-004).</summary>
    public TimeZoneInfo ResolveZone(string? deviceTimezone, string? homeTimezone)
    {
        foreach (var id in new[] { deviceTimezone, homeTimezone, options.Value.DefaultTimeZone })
        {
            if (TryFindZone(id, out var zone))
            {
                return zone;
            }
        }

        return TimeZoneInfo.Utc;
    }

    public static bool TryFindZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100)
        {
            return false;
        }

        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    private static DateTime LocalTime(DateTimeOffset at, int? utcOffsetMinutes, TimeZoneInfo zone) =>
        utcOffsetMinutes is { } offset && Math.Abs(offset) <= MaxOffsetMinutes
            ? at.ToOffset(TimeSpan.FromMinutes(offset)).DateTime
            : TimeZoneInfo.ConvertTime(at, zone).DateTime;

    private static bool InWindow(DateTime local, IReadOnlyList<ScheduleWindow> windows)
    {
        var day = (int)local.DayOfWeek;
        var time = TimeOnly.FromDateTime(local);
        foreach (var w in windows)
        {
            if (w.Start < w.End)
            {
                if (w.DayOfWeek == day && time >= w.Start && time < w.End)
                {
                    return true;
                }
            }
            else if ((w.DayOfWeek == day && time >= w.Start) || (w.DayOfWeek == (day + 6) % 7 && time < w.End))
            {
                // Crosses midnight: the late part is on the window's day, the early part on the day after it,
                // so for the early part the window's day is yesterday.
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<DateTimeOffset> ScheduleBoundaries(DateTimeOffset now, TimeZoneInfo zone, IReadOnlyList<ScheduleWindow> windows)
    {
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        for (var i = -1; i <= 8; i++)
        {
            var date = today.AddDays(i);
            foreach (var w in windows.Where(w => w.DayOfWeek == (int)date.DayOfWeek))
            {
                var start = date + w.Start.ToTimeSpan();
                var end = (w.Start < w.End ? date : date.AddDays(1)) + w.End.ToTimeSpan();
                foreach (var boundary in new[] { ToUtc(start, zone), ToUtc(end, zone) })
                {
                    if (boundary > now)
                    {
                        yield return boundary;
                    }
                }
            }
        }
    }

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            // Skipped by a daylight-saving jump forward.
            unspecified = unspecified.AddHours(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), TimeSpan.Zero);
    }
}
