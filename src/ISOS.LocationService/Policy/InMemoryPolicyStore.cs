using ISOS.LocationService.Ingestion;

namespace ISOS.LocationService.Policy;

/// <summary>Development and test store. Same rules as the PostgreSQL store; not durable.</summary>
public sealed class InMemoryPolicyStore(InMemoryLocationPointStore points) : IPolicyStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ClientPolicy> _clients = [];
    private readonly Dictionary<(string Party, string Client), ConsentRecord> _consents = [];
    private readonly Dictionary<(string Party, string Client), SharingPreference> _preferences = [];
    private readonly Dictionary<(string Party, string Client), List<EventWindow>> _eventWindows = [];
    private readonly Dictionary<(string Party, string Client, string Device), (string State, DateTimeOffset LeaseExpiresAt)> _devices = [];

    /// <summary>Test helper: sets a client's configuration.</summary>
    public void SetClientPolicy(ClientPolicy policy)
    {
        lock (_gate) { _clients[policy.ClientId] = policy; }
    }

    public string? DeviceState(string partyId, string clientId, string deviceId)
    {
        lock (_gate) { return _devices.TryGetValue((partyId, clientId, deviceId), out var d) ? d.State : null; }
    }

    public Task<ClientPolicy?> GetClientPolicyAsync(string clientId, CancellationToken cancellationToken)
    {
        lock (_gate) { return Task.FromResult(_clients.GetValueOrDefault(clientId)); }
    }

    public Task<UserPolicyState> GetUserStateAsync(
        string partyId, string clientId, DateTimeOffset eventWindowsSince, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var key = (partyId, clientId);
            IReadOnlyList<EventWindow> windows = _eventWindows.TryGetValue(key, out var all)
                ? all.Where(w => w.ExpiresAt > eventWindowsSince).OrderBy(w => w.StartsAt).ToList()
                : [];
            return Task.FromResult(new UserPolicyState(
                _consents.GetValueOrDefault(key), _preferences.GetValueOrDefault(key), windows));
        }
    }

    public Task GrantConsentAsync(string partyId, string clientId, string disclosureVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var key = (partyId, clientId);
            if (_consents.TryGetValue(key, out var current))
            {
                if (current.Status == ConsentStatuses.Granted && current.DisclosureVersion == disclosureVersion)
                {
                    return Task.CompletedTask;
                }

                _consents[key] = current with { Status = ConsentStatuses.Granted, DisclosureVersion = disclosureVersion, GrantedAt = now };
            }
            else
            {
                _consents[key] = new ConsentRecord(ConsentStatuses.Granted, disclosureVersion, now, null, null);
            }
        }

        return Task.CompletedTask;
    }

    public Task RevokeConsentAsync(string partyId, string clientId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var key = (partyId, clientId);
            if (!_consents.TryGetValue(key, out var current) || current.Status != ConsentStatuses.Granted)
            {
                return Task.CompletedTask;
            }

            _consents[key] = current with { Status = ConsentStatuses.Revoked, RevokedAt = now, VisibleFrom = now };
            points.RemoveSharedLatest(partyId);
            foreach (var device in _devices.Keys.Where(k => k.Party == partyId && k.Client == clientId).ToList())
            {
                _devices[device] = (DeviceSessionStates.Ended, _devices[device].LeaseExpiresAt);
            }
        }

        return Task.CompletedTask;
    }

    public Task SavePreferenceAsync(
        string partyId, string clientId, SharingPreference preference, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate) { _preferences[(partyId, clientId)] = preference; }
        return Task.CompletedTask;
    }

    public Task UpsertDeviceStateAsync(DeviceStateUpdate update, CancellationToken cancellationToken)
    {
        var state = update.OsPermission == OsPermissions.Always && update.PreciseLocation
            ? DeviceSessionStates.Active
            : DeviceSessionStates.SuspendedOsPermission;
        lock (_gate) { _devices[(update.PartyId, update.ClientId, update.DeviceId)] = (state, update.LeaseExpiresAt); }
        return Task.CompletedTask;
    }

    public Task EndDeviceSessionAsync(string partyId, string clientId, string deviceId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var key = (partyId, clientId, deviceId);
            if (_devices.TryGetValue(key, out var device))
            {
                _devices[key] = (DeviceSessionStates.Ended, device.LeaseExpiresAt);
            }
        }

        return Task.CompletedTask;
    }

    public Task AddEventWindowAsync(string partyId, string clientId, EventWindow window, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var key = (partyId, clientId);
            if (!_eventWindows.TryGetValue(key, out var list))
            {
                _eventWindows[key] = list = [];
            }

            list.Add(window);
        }

        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetFenceAsync(string partyId, string? clientId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            DateTimeOffset? fence = null;
            foreach (var ((party, client), consent) in _consents)
            {
                if (party != partyId || (clientId is not null && client != clientId))
                {
                    continue;
                }

                fence = Later(fence, consent.VisibleFrom);
                if (_clients.TryGetValue(client, out var policy))
                {
                    fence = Later(fence, policy.VisibleFrom);
                }
            }

            return Task.FromResult(fence);
        }
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
