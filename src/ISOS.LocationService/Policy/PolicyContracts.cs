namespace ISOS.LocationService.Policy;

/// <param name="AllowedNow">Whether the device may capture and send live-sharing points right now.</param>
/// <param name="NextEvaluationAt">When the answer may change by itself (a window opening or closing). The device asks again then.</param>
/// <param name="ValidUntil">The device must stop sharing when this passes unless it has refreshed the policy. Null when not allowed.</param>
/// <param name="ConsentRequired">True when the client allows sharing but the person has not agreed to the current disclosure.</param>
/// <param name="DisclosureVersion">The disclosure version the person must have seen. Send it with the consent.</param>
/// <param name="ClientId">The client this policy is for.</param>
/// <param name="Etag">Changes whenever the policy changes. Also sent as the ETag header; use it with If-None-Match.</param>
/// <param name="HeartbeatSeconds">How often a stationary device sends a point.</param>
/// <param name="PrimaryType">The type the person chose (ALWAYS, SCHEDULE, ACTIVE_TRAVEL), or null.</param>
/// <param name="Schedule">The person's weekly windows in their local time, so the device can follow them offline.</param>
/// <param name="EventWindow">The event window (for example after an AEC) that is open now, or null.</param>
/// <param name="SharingEnabled">Whether the client allows live sharing at all.</param>
/// <param name="EnabledTypes">The types the client offers. Empty when sharing is not enabled.</param>
public sealed record PolicyResponse(
    bool AllowedNow,
    DateTimeOffset? NextEvaluationAt,
    DateTimeOffset? ValidUntil,
    bool ConsentRequired,
    string DisclosureVersion,
    string ClientId,
    string Etag,
    int HeartbeatSeconds,
    string? PrimaryType,
    IReadOnlyList<ScheduleWindowDto> Schedule,
    EventWindowDto? EventWindow,
    bool SharingEnabled,
    IReadOnlyList<string> EnabledTypes);

public sealed record EventWindowDto(string TriggerType, DateTimeOffset StartsAt, DateTimeOffset ExpiresAt);

/// <param name="DayOfWeek">0 = Sunday ... 6 = Saturday.</param>
/// <param name="StartTime">Local time, HH:mm.</param>
/// <param name="EndTime">Local time, HH:mm. Before the start time means the window runs past midnight.</param>
public sealed record ScheduleWindowDto(int? DayOfWeek, string? StartTime, string? EndTime);

/// <param name="Status">GRANTED or REVOKED.</param>
/// <param name="DisclosureVersion">Required for GRANTED: the version of the disclosure the person accepted.</param>
public sealed record ConsentRequest(string? Status, string? DisclosureVersion);

/// <param name="Platform">ios or android.</param>
/// <param name="OsPermission">ALWAYS, WHEN_IN_USE, DENIED or OFF.</param>
/// <param name="PreciseLocation">Whether precise location is on.</param>
/// <param name="Timezone">The device's IANA time zone.</param>
public sealed record DeviceStateRequest(string? Platform, string? OsPermission, bool? PreciseLocation, string? Timezone);

/// <param name="State">ACTIVE, or SUSPENDED_OS_PERMISSION when the OS permission is not enough to share.</param>
/// <param name="LeaseExpiresAt">When the sharing lease ends unless the device reports again.</param>
public sealed record DeviceStateResponse(string State, DateTimeOffset LeaseExpiresAt);

/// <param name="PrimaryType">ALWAYS, SCHEDULE or ACTIVE_TRAVEL. Must be one the client offers.</param>
/// <param name="HomeTimezone">Optional IANA zone used when the device does not report one.</param>
/// <param name="Schedule">Weekly windows. At least one is required for SCHEDULE. Kept when another type is chosen.</param>
public sealed record PreferenceRequest(string? PrimaryType, string? HomeTimezone, IReadOnlyList<ScheduleWindowDto>? Schedule);
