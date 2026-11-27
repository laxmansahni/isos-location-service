namespace ISOS.LocationService.Policy;

public static class SharingTypes
{
    public const string Always = "ALWAYS";
    public const string Schedule = "SCHEDULE";
    public const string ActiveTravel = "ACTIVE_TRAVEL";

    /// <summary>The types a person can choose as their primary type (ADR-004). The event window is additive.</summary>
    public static readonly IReadOnlyList<string> Primary = [Always, Schedule, ActiveTravel];
}

public static class ConsentStatuses
{
    public const string Granted = "GRANTED";
    public const string Revoked = "REVOKED";
}

public static class EventTriggers
{
    public const string Aec = "AEC";
    public const string SpecialAdvisory = "SPECIAL_ADVISORY";
}

public static class OsPermissions
{
    public const string Always = "ALWAYS";
    public const string WhenInUse = "WHEN_IN_USE";
    public const string Denied = "DENIED";
    public const string Off = "OFF";

    public static readonly IReadOnlyList<string> All = [Always, WhenInUse, Denied, Off];
}

public static class DeviceSessionStates
{
    public const string Active = "ACTIVE";
    public const string SuspendedOsPermission = "SUSPENDED_OS_PERMISSION";
    public const string Ended = "ENDED";
}

/// <summary>Why live sharing is not allowed. These are the per-point rejection reasons and the 403 codes.</summary>
public static class PolicyDenials
{
    public const string SharingNotAllowed = "SHARING_NOT_ALLOWED";
    public const string ConsentRequired = "CONSENT_REQUIRED";
    public const string CapturedBeforeConsent = "CAPTURED_BEFORE_CONSENT";
    public const string NoActiveSharingType = "NO_ACTIVE_SHARING_TYPE";
    public const string OutsideSharingWindow = "OUTSIDE_SHARING_WINDOW";
}

/// <summary>A client's configuration. <c>VisibleFrom</c> is its visibility fence (ADR-005), null when none is set.</summary>
public sealed record ClientPolicy(
    string ClientId,
    bool SharingEnabled,
    bool TypeAlways,
    bool TypeSchedule,
    bool TypeActiveTravel,
    bool TypeEventWindow,
    bool AllowUserViewHistory,
    int RetentionDays,
    int EventWindowMinutes,
    DateTimeOffset? VisibleFrom,
    int Version)
{
    public bool IsEnabled(string primaryType) => primaryType switch
    {
        SharingTypes.Always => TypeAlways,
        SharingTypes.Schedule => TypeSchedule,
        SharingTypes.ActiveTravel => TypeActiveTravel,
        _ => false,
    };

    public IReadOnlyList<string> EnabledTypes() => SharingEnabled
        ? SharingTypes.Primary.Where(IsEnabled).ToList()
        : [];
}

/// <summary>A person's consent. <c>VisibleFrom</c> is their visibility fence, set when consent is revoked.</summary>
public sealed record ConsentRecord(
    string Status,
    string DisclosureVersion,
    DateTimeOffset? GrantedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? VisibleFrom);

/// <summary>A weekly window in local time. <c>DayOfWeek</c> is 0 = Sunday ... 6 = Saturday; an <c>End</c> before <c>Start</c> crosses midnight.</summary>
public sealed record ScheduleWindow(int DayOfWeek, TimeOnly Start, TimeOnly End);

public sealed record SharingPreference(string PrimaryType, string? HomeTimezone, IReadOnlyList<ScheduleWindow> Schedule);

public sealed record EventWindow(string TriggerType, string? SourceRef, DateTimeOffset StartsAt, DateTimeOffset ExpiresAt);

public sealed record UserPolicyState(
    ConsentRecord? Consent,
    SharingPreference? Preference,
    IReadOnlyList<EventWindow> EventWindows);

/// <summary>Everything needed to decide whether a point is allowed, read once per request.</summary>
public sealed record PolicySnapshot(ClientPolicy Client, UserPolicyState User);

public sealed record DeviceStateUpdate(
    string PartyId,
    string ClientId,
    string DeviceId,
    string? Platform,
    string OsPermission,
    bool PreciseLocation,
    string? Timezone,
    DateTimeOffset Now,
    DateTimeOffset LeaseExpiresAt);
