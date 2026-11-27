namespace ISOS.LocationService.Policy;

public interface IPolicyStore
{
    /// <summary>The client's configuration, or null when it has none (the configured defaults then apply).</summary>
    Task<ClientPolicy?> GetClientPolicyAsync(string clientId, CancellationToken cancellationToken);

    /// <summary>The person's consent, preference and event windows. Event windows that ended before <paramref name="eventWindowsSince"/> are left out.</summary>
    Task<UserPolicyState> GetUserStateAsync(
        string partyId, string clientId, DateTimeOffset eventWindowsSince, CancellationToken cancellationToken);

    /// <summary>
    /// Records consent for the given disclosure version. Does nothing when the same version is already granted, so a
    /// retry does not move <c>granted_at</c>.
    /// </summary>
    Task GrantConsentAsync(string partyId, string clientId, string disclosureVersion, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Revokes consent (ADR-005), in one transaction: marks it revoked and sets the person's visibility fence, deletes
    /// their latest live-sharing position and ends their device sessions. Does nothing when consent is not granted.
    /// </summary>
    Task RevokeConsentAsync(string partyId, string clientId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Replaces the person's type, home zone and schedule.</summary>
    Task SavePreferenceAsync(
        string partyId, string clientId, SharingPreference preference, DateTimeOffset now, CancellationToken cancellationToken);

    Task UpsertDeviceStateAsync(DeviceStateUpdate update, CancellationToken cancellationToken);

    /// <summary>Marks the device session ended (logout). Does nothing for an unknown device.</summary>
    Task EndDeviceSessionAsync(string partyId, string clientId, string deviceId, CancellationToken cancellationToken);

    /// <summary>Opens an event window, for the future AEC consumer (ADR-004).</summary>
    Task AddEventWindowAsync(string partyId, string clientId, EventWindow window, CancellationToken cancellationToken);

    /// <summary>
    /// The latest visibility fence that applies to a person: their consent's and their client's. Queries return only
    /// points captured after it (ADR-005). Without a client, every client the person has consented to counts.
    /// </summary>
    Task<DateTimeOffset?> GetFenceAsync(string partyId, string? clientId, CancellationToken cancellationToken);
}
