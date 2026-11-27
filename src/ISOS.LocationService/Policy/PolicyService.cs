using System.Security.Cryptography;
using System.Text;
using ISOS.LocationService.Ingestion;
using Microsoft.Extensions.Options;

namespace ISOS.LocationService.Policy;

/// <summary>Loads a person's policy and turns it into the response the device uses (LLD 4.2).</summary>
public sealed class PolicyService(
    IPolicyStore store,
    PolicyEvaluator evaluator,
    IOptions<PolicyOptions> options,
    IOptions<IngestionOptions> ingestionOptions,
    TimeProvider clock)
{
    public async Task<PolicySnapshot> LoadAsync(string partyId, string clientId, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var client = await store.GetClientPolicyAsync(clientId, cancellationToken) ?? DefaultClient(clientId);
        // Buffered points can be up to the retention window old, so keep event windows that long.
        var user = await store.GetUserStateAsync(
            partyId, clientId, now - TimeSpan.FromDays(ingestionOptions.Value.RetentionDays), cancellationToken);
        return new PolicySnapshot(client, user);
    }

    public PolicyResponse Describe(PolicySnapshot snapshot, string? deviceTimezone)
    {
        var now = clock.GetUtcNow();
        var o = options.Value;
        var allowed = evaluator.Evaluate(snapshot, now, utcOffsetMinutes: null, deviceTimezone).Allowed;
        var next = evaluator.NextEvaluationAt(snapshot, now, deviceTimezone);
        var preference = snapshot.User.Preference;
        var schedule = preference?.Schedule
            .Select(w => new ScheduleWindowDto(w.DayOfWeek, w.Start.ToString("HH:mm"), w.End.ToString("HH:mm")))
            .ToList() ?? [];
        var open = snapshot.Client.TypeEventWindow
            ? snapshot.User.EventWindows.FirstOrDefault(w => w.StartsAt <= now && now < w.ExpiresAt)
            : null;
        var consentRequired = snapshot.Client.SharingEnabled && !evaluator.HasValidConsent(snapshot.User.Consent);

        return new PolicyResponse(
            AllowedNow: allowed,
            NextEvaluationAt: next,
            ValidUntil: allowed ? now + o.LeaseDuration : null,
            ConsentRequired: consentRequired,
            DisclosureVersion: o.DisclosureVersion,
            ClientId: snapshot.Client.ClientId,
            Etag: ComputeEtag(snapshot, allowed, next, consentRequired),
            HeartbeatSeconds: o.HeartbeatSeconds,
            PrimaryType: preference?.PrimaryType,
            Schedule: schedule,
            EventWindow: open is null ? null : new EventWindowDto(open.TriggerType, open.StartsAt, open.ExpiresAt),
            SharingEnabled: snapshot.Client.SharingEnabled,
            EnabledTypes: snapshot.Client.EnabledTypes());
    }

    public ClientPolicy DefaultClient(string clientId)
    {
        var d = options.Value.DefaultClient;
        return new ClientPolicy(
            clientId, d.SharingEnabled, d.TypeAlways, d.TypeSchedule, d.TypeActiveTravel, d.TypeEventWindow,
            d.AllowUserViewHistory, d.RetentionDays, d.EventWindowMinutes, VisibleFrom: null, Version: 0);
    }

    // Built from what the policy says, not from the lease, so it stays the same while nothing changes.
    private static string ComputeEtag(PolicySnapshot s, bool allowed, DateTimeOffset? next, bool consentRequired)
    {
        var p = s.User.Preference;
        var text = string.Join('|',
            s.Client.ClientId, s.Client.Version, s.Client.SharingEnabled, string.Join(',', s.Client.EnabledTypes()),
            s.Client.TypeEventWindow, allowed, next?.UtcTicks, consentRequired, s.User.Consent?.GrantedAt?.UtcTicks,
            p?.PrimaryType, p?.HomeTimezone,
            string.Join(',', p?.Schedule.Select(w => $"{w.DayOfWeek}@{w.Start}-{w.End}") ?? []));
        return $"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32]}\"";
    }
}
