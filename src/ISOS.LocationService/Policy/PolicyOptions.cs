namespace ISOS.LocationService.Policy;

public sealed class PolicyOptions
{
    public const string SectionName = "Policy";

    /// <summary>
    /// The disclosure text the app must show before consent. A consent given for another version no longer counts,
    /// so raising it asks everyone to accept again.
    /// </summary>
    public string DisclosureVersion { get; set; } = "1";

    /// <summary>How often a stationary device sends a point (Significant-Change Location Capture story: 12 h).</summary>
    public int HeartbeatSeconds { get; set; } = 12 * 60 * 60;

    /// <summary>
    /// How long a device may keep sharing without asking the server again (<c>validUntil</c>). Placeholder of 48 h,
    /// the offline buffer of ADR-005, until Product sets the lease (HLD Q3).
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromHours(48);

    /// <summary>Last step of the time zone resolution (ADR-004): device zone, then home zone, then this.</summary>
    public string DefaultTimeZone { get; set; } = "UTC";

    /// <summary>
    /// A device clock a little behind the server must not make the first points after consent look as if they were
    /// captured before it.
    /// </summary>
    public TimeSpan ConsentClockSkew { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Used for a client that has no row in client_location_policy. Sharing is off unless enabled.</summary>
    public ClientPolicyDefaults DefaultClient { get; set; } = new();
}

public sealed class ClientPolicyDefaults
{
    public bool SharingEnabled { get; set; }

    public bool TypeAlways { get; set; } = true;

    public bool TypeSchedule { get; set; } = true;

    public bool TypeActiveTravel { get; set; } = true;

    public bool TypeEventWindow { get; set; } = true;

    public bool AllowUserViewHistory { get; set; } = true;

    public int RetentionDays { get; set; } = 14;

    public int EventWindowMinutes { get; set; } = 60;
}
