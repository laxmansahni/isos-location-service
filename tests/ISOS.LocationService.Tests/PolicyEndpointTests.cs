using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ISOS.LocationService.Identity;
using ISOS.LocationService.Policy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ISOS.LocationService.Tests;

public class PolicyEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Base = "/v1/locationtracking";
    private const string Client = SharingTestSetup.ClientId;
    private const string DisabledClient = "client-sharing-off";
    private const string NoScheduleClient = "client-no-schedule";

    private readonly WebApplicationFactory<Program> _factory;

    public PolicyEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = SharingTestSetup.Configure(factory);
        var store = _factory.Services.GetRequiredService<InMemoryPolicyStore>();
        store.SetClientPolicy(new ClientPolicy(DisabledClient, false, true, true, true, true, true, 14, 60, null, 1));
        store.SetClientPolicy(new ClientPolicy(NoScheduleClient, true, true, false, true, true, true, 14, 60, null, 1));
    }

    private static string NewParty() => $"IND_{Guid.NewGuid()}";

    private static HttpRequestMessage Call(
        HttpMethod method, string url, string? party, string? client = Client, object? body = null, string? permissions = null, string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(method, $"{Base}{url}");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (party is not null)
        {
            request.Headers.Add(DevelopmentHeaderCallerContext.PartyIdHeader, party);
        }

        if (client is not null)
        {
            request.Headers.Add(DevelopmentHeaderCallerContext.ClientIdHeader, client);
        }

        if (permissions is not null)
        {
            request.Headers.Add(DevelopmentHeaderCallerContext.PermissionsHeader, permissions);
        }

        if (ifNoneMatch is not null)
        {
            request.Headers.Add("If-None-Match", ifNoneMatch);
        }

        return request;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(HttpRequestMessage request)
    {
        var response = await _factory.CreateClient().SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement);
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> GrantAsync(string party, string client = Client, string version = "1") =>
        SendAsync(Call(HttpMethod.Put, "/consent", party, client, new { status = "GRANTED", disclosureVersion = version }));

    private Task<(HttpStatusCode Status, JsonElement Body)> RevokeAsync(string party, string client = Client) =>
        SendAsync(Call(HttpMethod.Put, "/consent", party, client, new { status = "REVOKED" }));

    private Task<(HttpStatusCode Status, JsonElement Body)> ChooseAsync(string party, string type, object? schedule = null, string client = Client) =>
        SendAsync(Call(HttpMethod.Put, "/users/me/preference", party, client, new { primaryType = type, schedule = schedule ?? Array.Empty<object>() }));

    private static object Point(DateTimeOffset capturedAt, bool shared = true) => new
    {
        pointId = Guid.NewGuid(), capturedAt, utcOffsetMinutes = 0, latitude = 51.5, longitude = -0.12, accuracy = 10.0, shared,
    };

    private Task<(HttpStatusCode Status, JsonElement Body)> SendPointsAsync(string party, string? client, params object[] points) =>
        SendAsync(Call(HttpMethod.Post, "/points", party, client, new { points, device = new { id = "device-1", platform = "ios", timezone = "UTC" } }));

    private static object[] EveryDay() => Enumerable.Range(0, 7).SelectMany(d => new object[]
    {
        new { dayOfWeek = d, startTime = "00:00", endTime = "12:00" },
        new { dayOfWeek = d, startTime = "12:00", endTime = "00:00" }, // crosses midnight: the rest of the day
    }).ToArray();

    // --- GET /policy ---

    [Fact]
    public async Task Policy_needs_a_caller_and_a_client()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(Call(HttpMethod.Get, "/policy", null))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Call(HttpMethod.Get, "/policy", NewParty(), client: null))).Status);
    }

    [Fact]
    public async Task New_person_must_consent_and_choose_a_type_before_sharing_is_allowed()
    {
        var party = NewParty();

        var (_, fresh) = await SendAsync(Call(HttpMethod.Get, "/policy", party));
        await GrantAsync(party);
        var (_, consented) = await SendAsync(Call(HttpMethod.Get, "/policy", party));
        var (status, chosen) = await ChooseAsync(party, "ALWAYS");

        Assert.False(fresh.GetProperty("allowedNow").GetBoolean());
        Assert.True(fresh.GetProperty("consentRequired").GetBoolean());
        Assert.True(fresh.GetProperty("sharingEnabled").GetBoolean());
        Assert.Equal("1", fresh.GetProperty("disclosureVersion").GetString());
        Assert.Equal(Client, fresh.GetProperty("clientId").GetString());
        Assert.Equal(43200, fresh.GetProperty("heartbeatSeconds").GetInt32());
        Assert.Equal(["ALWAYS", "SCHEDULE", "ACTIVE_TRAVEL"], fresh.GetProperty("enabledTypes").EnumerateArray().Select(t => t.GetString()));
        Assert.False(consented.GetProperty("consentRequired").GetBoolean());
        Assert.False(consented.GetProperty("allowedNow").GetBoolean()); // no type chosen yet
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(chosen.GetProperty("allowedNow").GetBoolean());
        Assert.Equal("ALWAYS", chosen.GetProperty("primaryType").GetString());
        Assert.True(chosen.GetProperty("validUntil").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddHours(47));
    }

    [Fact]
    public async Task Policy_supports_etag_and_changes_it_when_the_policy_changes()
    {
        var party = NewParty();
        await GrantAsync(party);
        await ChooseAsync(party, "ALWAYS");
        var client = _factory.CreateClient();

        var first = await client.SendAsync(Call(HttpMethod.Get, "/policy", party));
        var etag = first.Headers.ETag!.Tag;
        var unchanged = await client.SendAsync(Call(HttpMethod.Get, "/policy", party, ifNoneMatch: etag));
        await ChooseAsync(party, "SCHEDULE", EveryDay());
        var changed = await client.SendAsync(Call(HttpMethod.Get, "/policy", party, ifNoneMatch: etag));

        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(etag, changed.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Client_without_sharing_gets_no_types_and_cannot_be_consented_to()
    {
        var party = NewParty();

        var (_, policy) = await SendAsync(Call(HttpMethod.Get, "/policy", party, DisabledClient));
        var consent = await GrantAsync(party, DisabledClient);
        var choose = await ChooseAsync(party, "ALWAYS", client: DisabledClient);

        Assert.False(policy.GetProperty("sharingEnabled").GetBoolean());
        Assert.False(policy.GetProperty("consentRequired").GetBoolean());
        Assert.Equal(0, policy.GetProperty("enabledTypes").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, consent.Status);
        Assert.Equal("SHARING_NOT_ALLOWED", consent.Body.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, choose.Status);
    }

    // --- PUT /consent ---

    [Fact]
    public async Task Consent_needs_the_current_disclosure_version_and_a_valid_status()
    {
        var party = NewParty();

        Assert.Equal(HttpStatusCode.BadRequest, (await GrantAsync(party, version: "0")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Call(HttpMethod.Put, "/consent", party, body: new { status = "GRANTED" }))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Call(HttpMethod.Put, "/consent", party, body: new { status = "MAYBE" }))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(Call(HttpMethod.Put, "/consent", null, body: new { status = "REVOKED" }))).Status);
    }

    [Fact]
    public async Task Granting_twice_keeps_the_first_grant_time_and_revoking_twice_is_harmless()
    {
        var party = NewParty();
        var store = _factory.Services.GetRequiredService<IPolicyStore>();

        await GrantAsync(party);
        var first = (await store.GetUserStateAsync(party, Client, DateTimeOffset.MinValue, default)).Consent!;
        await Task.Delay(20);
        await GrantAsync(party);
        var second = (await store.GetUserStateAsync(party, Client, DateTimeOffset.MinValue, default)).Consent!;
        await RevokeAsync(party);
        var revoked = (await store.GetUserStateAsync(party, Client, DateTimeOffset.MinValue, default)).Consent!;
        var again = await RevokeAsync(party);
        var afterAgain = (await store.GetUserStateAsync(party, Client, DateTimeOffset.MinValue, default)).Consent!;

        Assert.Equal(first.GrantedAt, second.GrantedAt);
        Assert.Equal(ConsentStatuses.Revoked, revoked.Status);
        Assert.NotNull(revoked.VisibleFrom);
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(revoked.VisibleFrom, afterAgain.VisibleFrom);
    }

    [Fact]
    public async Task Revoking_without_ever_consenting_is_harmless()
    {
        var (status, body) = await RevokeAsync(NewParty());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("allowedNow").GetBoolean());
    }

    // --- PUT /users/me/preference ---

    [Fact]
    public async Task Preference_is_validated()
    {
        var party = NewParty();
        var windowOk = new[] { new { dayOfWeek = 1, startTime = "09:00", endTime = "17:00" } };

        Assert.Equal(HttpStatusCode.BadRequest, (await ChooseAsync(party, "SOMETIMES")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChooseAsync(party, "SCHEDULE")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChooseAsync(party, "SCHEDULE", new[] { new { dayOfWeek = 7, startTime = "09:00", endTime = "17:00" } })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChooseAsync(party, "SCHEDULE", new[] { new { dayOfWeek = 1, startTime = "9am", endTime = "17:00" } })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChooseAsync(party, "SCHEDULE", new[] { new { dayOfWeek = 1, startTime = "09:00", endTime = "09:00" } })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Call(HttpMethod.Put, "/users/me/preference", party, body: new { primaryType = "ALWAYS", homeTimezone = "Mars/Olympus" }))).Status);
        Assert.Equal(HttpStatusCode.OK, (await ChooseAsync(party, "SCHEDULE", windowOk)).Status);
    }

    [Fact]
    public async Task Type_the_client_does_not_offer_is_forbidden()
    {
        var (status, body) = await ChooseAsync(NewParty(), "SCHEDULE", EveryDay(), NoScheduleClient);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("TYPE_NOT_ENABLED", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Schedule_inside_a_window_is_allowed_and_outside_is_not_and_the_schedule_is_kept_for_other_types()
    {
        var party = NewParty();
        await GrantAsync(party);
        var otherDay = ((int)DateTimeOffset.UtcNow.DayOfWeek + 3) % 7;

        var (_, always) = await ChooseAsync(party, "SCHEDULE", EveryDay());
        var (_, outside) = await ChooseAsync(party, "SCHEDULE", new[] { new { dayOfWeek = otherDay, startTime = "00:00", endTime = "12:00" } });
        var (_, switched) = await ChooseAsync(party, "ALWAYS", new[] { new { dayOfWeek = otherDay, startTime = "00:00", endTime = "12:00" } });

        Assert.True(always.GetProperty("allowedNow").GetBoolean());
        Assert.Equal(14, always.GetProperty("schedule").GetArrayLength());
        Assert.False(outside.GetProperty("allowedNow").GetBoolean());
        Assert.True(outside.GetProperty("nextEvaluationAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        Assert.Equal(JsonValueKind.Null, outside.GetProperty("validUntil").ValueKind);
        Assert.True(switched.GetProperty("allowedNow").GetBoolean());
        Assert.Equal(1, switched.GetProperty("schedule").GetArrayLength());
    }

    [Fact]
    public async Task Open_event_window_allows_sharing_and_is_reported()
    {
        var party = NewParty();
        await GrantAsync(party);
        var otherDay = ((int)DateTimeOffset.UtcNow.DayOfWeek + 3) % 7;
        await ChooseAsync(party, "SCHEDULE", new[] { new { dayOfWeek = otherDay, startTime = "00:00", endTime = "12:00" } });
        var now = DateTimeOffset.UtcNow;
        await _factory.Services.GetRequiredService<IPolicyStore>().AddEventWindowAsync(
            party, Client, new EventWindow(EventTriggers.Aec, "aec-1", now.AddMinutes(-5), now.AddMinutes(55)), default);

        var (_, policy) = await SendAsync(Call(HttpMethod.Get, "/policy", party));

        Assert.True(policy.GetProperty("allowedNow").GetBoolean());
        Assert.Equal("AEC", policy.GetProperty("eventWindow").GetProperty("triggerType").GetString());
        Assert.Equal(now.AddMinutes(55), policy.GetProperty("nextEvaluationAt").GetDateTimeOffset(), TimeSpan.FromSeconds(1));
    }

    // --- device state and session ---

    [Fact]
    public async Task Device_state_is_active_only_with_always_permission_and_precise_location()
    {
        var party = NewParty();
        var store = _factory.Services.GetRequiredService<InMemoryPolicyStore>();

        async Task<(HttpStatusCode Status, JsonElement Body)> Report(string permission, bool precise) => await SendAsync(
            Call(HttpMethod.Put, "/devices/phone-1/state", party, body: new { platform = "ios", osPermission = permission, preciseLocation = precise, timezone = "Europe/London" }));

        var active = await Report("ALWAYS", true);
        var imprecise = await Report("ALWAYS", false);
        var foregroundOnly = await Report("WHEN_IN_USE", true);
        var denied = await Report("DENIED", true);
        var back = await Report("ALWAYS", true);

        Assert.Equal("ACTIVE", active.Body.GetProperty("state").GetString());
        Assert.Equal("SUSPENDED_OS_PERMISSION", imprecise.Body.GetProperty("state").GetString());
        Assert.Equal("SUSPENDED_OS_PERMISSION", foregroundOnly.Body.GetProperty("state").GetString());
        Assert.Equal("SUSPENDED_OS_PERMISSION", denied.Body.GetProperty("state").GetString());
        Assert.Equal("ACTIVE", back.Body.GetProperty("state").GetString());
        Assert.Equal("ACTIVE", store.DeviceState(party, Client, "phone-1"));
        Assert.True(active.Body.GetProperty("leaseExpiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddHours(47));
    }

    [Fact]
    public async Task Device_state_does_not_change_consent()
    {
        var party = NewParty();
        await GrantAsync(party);
        await ChooseAsync(party, "ALWAYS");

        await SendAsync(Call(HttpMethod.Put, "/devices/phone-1/state", party, body: new { osPermission = "DENIED", preciseLocation = false }));
        var (_, policy) = await SendAsync(Call(HttpMethod.Get, "/policy", party));

        Assert.True(policy.GetProperty("allowedNow").GetBoolean());
        Assert.False(policy.GetProperty("consentRequired").GetBoolean());
    }

    [Fact]
    public async Task Device_state_is_validated()
    {
        var party = NewParty();

        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Call(HttpMethod.Put, "/devices/d/state", party, body: new { osPermission = "MAYBE", preciseLocation = true }))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Call(HttpMethod.Put, "/devices/d/state", party, body: new { osPermission = "ALWAYS" }))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(Call(HttpMethod.Put, $"/devices/{new string('d', 101)}/state", party, body: new { osPermission = "ALWAYS", preciseLocation = true }))).Status);
    }

    [Fact]
    public async Task Logout_ends_only_that_device_and_can_be_repeated()
    {
        var party = NewParty();
        var store = _factory.Services.GetRequiredService<InMemoryPolicyStore>();
        foreach (var device in new[] { "phone-1", "tablet-1" })
        {
            await SendAsync(Call(HttpMethod.Put, $"/devices/{device}/state", party, body: new { osPermission = "ALWAYS", preciseLocation = true }));
        }

        var first = await SendAsync(Call(HttpMethod.Delete, "/devices/phone-1/session", party));
        var again = await SendAsync(Call(HttpMethod.Delete, "/devices/phone-1/session", party));
        var unknown = await SendAsync(Call(HttpMethod.Delete, "/devices/never-seen/session", party));

        Assert.Equal(HttpStatusCode.NoContent, first.Status);
        Assert.Equal(HttpStatusCode.NoContent, again.Status);
        Assert.Equal(HttpStatusCode.NoContent, unknown.Status);
        Assert.Equal("ENDED", store.DeviceState(party, Client, "phone-1"));
        Assert.Equal("ACTIVE", store.DeviceState(party, Client, "tablet-1"));
    }

    // --- enforcement in ingestion ---

    [Fact]
    public async Task Live_points_without_consent_get_403_with_the_policy()
    {
        var party = NewParty();

        var (status, body) = await SendPointsAsync(party, Client, Point(DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("CONSENT_REQUIRED", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("policy").GetProperty("consentRequired").GetBoolean());
        Assert.False(body.GetProperty("policy").GetProperty("allowedNow").GetBoolean());
    }

    [Fact]
    public async Task Live_points_for_a_client_without_sharing_get_403()
    {
        var (status, body) = await SendPointsAsync(NewParty(), DisabledClient, Point(DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("SHARING_NOT_ALLOWED", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Live_points_need_a_client()
    {
        var (status, _) = await SendPointsAsync(NewParty(), null, Point(DateTimeOffset.UtcNow.AddMinutes(-1)));

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Not_shared_points_need_no_consent_and_no_client()
    {
        var (status, body) = await SendPointsAsync(NewParty(), null, Point(DateTimeOffset.UtcNow.AddMinutes(-1), shared: false));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body.GetProperty("stored").GetInt32());
    }

    [Fact]
    public async Task In_a_mixed_batch_live_points_are_rejected_one_by_one_and_not_shared_points_are_kept()
    {
        var party = NewParty();
        var now = DateTimeOffset.UtcNow;

        var (status, body) = await SendPointsAsync(party, Client, Point(now.AddMinutes(-3), shared: false), Point(now.AddMinutes(-2)));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body.GetProperty("stored").GetInt32());
        Assert.Equal(1, body.GetProperty("rejected").GetInt32());
        Assert.Equal("CONSENT_REQUIRED", body.GetProperty("results")[1].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Consented_person_with_a_type_can_send_live_points()
    {
        var party = NewParty();
        await GrantAsync(party);
        await ChooseAsync(party, "ALWAYS");

        var (status, body) = await SendPointsAsync(party, Client, Point(DateTimeOffset.UtcNow.AddSeconds(-30)));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body.GetProperty("stored").GetInt32());
    }

    [Fact]
    public async Task Consented_person_without_a_type_has_each_live_point_rejected()
    {
        var party = NewParty();
        await GrantAsync(party);

        var (status, body) = await SendPointsAsync(party, Client, Point(DateTimeOffset.UtcNow.AddSeconds(-30)));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("NO_ACTIVE_SHARING_TYPE", body.GetProperty("results")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Point_captured_before_consent_is_rejected_and_one_captured_after_is_stored()
    {
        var party = NewParty();
        await GrantAsync(party);
        await ChooseAsync(party, "ALWAYS");
        var now = DateTimeOffset.UtcNow;

        var (_, body) = await SendPointsAsync(party, Client, Point(now.AddHours(-2)), Point(now.AddSeconds(-1)));

        Assert.Equal("CAPTURED_BEFORE_CONSENT", body.GetProperty("results")[0].GetProperty("reason").GetString());
        Assert.Equal("Stored", body.GetProperty("results")[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Point_outside_the_schedule_is_rejected()
    {
        var party = NewParty();
        await GrantAsync(party);
        var otherDay = ((int)DateTimeOffset.UtcNow.DayOfWeek + 3) % 7;
        await ChooseAsync(party, "SCHEDULE", new[] { new { dayOfWeek = otherDay, startTime = "00:00", endTime = "12:00" } });

        var (_, body) = await SendPointsAsync(party, Client, Point(DateTimeOffset.UtcNow.AddSeconds(-30)));

        Assert.Equal("OUTSIDE_SHARING_WINDOW", body.GetProperty("results")[0].GetProperty("reason").GetString());
    }

    // --- revoke ---

    [Fact]
    public async Task Revoking_stops_ingestion_removes_the_person_from_the_map_and_hides_earlier_history()
    {
        var party = NewParty();
        await GrantAsync(party);
        await ChooseAsync(party, "ALWAYS");
        await SendPointsAsync(party, Client, Point(DateTimeOffset.UtcNow.AddSeconds(-20)));
        await SendAsync(Call(HttpMethod.Put, "/devices/phone-1/state", party, body: new { osPermission = "ALWAYS", preciseLocation = true }));
        var own = (string url) => Call(HttpMethod.Get, url, party, permissions: "USRLOCHIS");
        var manager = Call(HttpMethod.Get, "/latest", "IND_manager", client: null, permissions: "MGRLOCHIS");
        var before = await SendAsync(own($"/users/{party}/history"));
        var onMapBefore = (await SendAsync(manager)).Body.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("userId").GetString() == party);
        await Task.Delay(20);

        var (revokeStatus, policy) = await RevokeAsync(party);

        var after = await SendAsync(own($"/users/{party}/history?range=24h"));
        var replay = await SendAsync(own($"/users/{party}/replay?range=24h"));
        var onMapAfter = (await SendAsync(Call(HttpMethod.Get, "/latest", "IND_manager", client: null, permissions: "MGRLOCHIS")))
            .Body.GetProperty("items").EnumerateArray().Any(i => i.GetProperty("userId").GetString() == party);
        var send = await SendPointsAsync(party, Client, Point(DateTimeOffset.UtcNow.AddSeconds(-1)));

        Assert.Equal(1, before.Body.GetProperty("points").GetArrayLength());
        Assert.True(onMapBefore);
        Assert.Equal(HttpStatusCode.OK, revokeStatus);
        Assert.False(policy.GetProperty("allowedNow").GetBoolean());
        Assert.True(policy.GetProperty("consentRequired").GetBoolean());
        Assert.Equal(0, after.Body.GetProperty("points").GetArrayLength());
        Assert.Equal(0, replay.Body.GetProperty("points").GetArrayLength());
        Assert.False(onMapAfter);
        Assert.Equal(HttpStatusCode.Forbidden, send.Status);
        Assert.Equal("CONSENT_REQUIRED", send.Body.GetProperty("code").GetString());
        Assert.Equal("ENDED", _factory.Services.GetRequiredService<InMemoryPolicyStore>().DeviceState(party, Client, "phone-1"));
    }

    [Fact]
    public async Task After_revoking_and_granting_again_only_new_points_are_visible()
    {
        var party = NewParty();
        await GrantAsync(party);
        await ChooseAsync(party, "ALWAYS");
        await SendPointsAsync(party, Client, Point(DateTimeOffset.UtcNow.AddSeconds(-30)));
        await Task.Delay(20);
        await RevokeAsync(party);
        await Task.Delay(20);
        await GrantAsync(party);

        // Captured after the fence set by the revoke, which is "now" on the server.
        var (_, stored) = await SendPointsAsync(party, Client, Point(DateTimeOffset.UtcNow.AddSeconds(2)));
        var (_, history) = await SendAsync(Call(HttpMethod.Get, $"/users/{party}/history?range=24h", party, permissions: "USRLOCHIS"));

        Assert.Equal(1, stored.GetProperty("stored").GetInt32());
        Assert.Equal(1, history.GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task Revoking_keeps_the_not_shared_latest_position()
    {
        var party = NewParty();
        await GrantAsync(party);
        await ChooseAsync(party, "ALWAYS");
        await SendPointsAsync(party, Client, Point(DateTimeOffset.UtcNow.AddMinutes(-2), shared: false));
        var latest = _factory.Services.GetRequiredService<ISOS.LocationService.Ingestion.InMemoryLocationPointStore>();

        await RevokeAsync(party);

        Assert.NotNull(latest.Latest(party));
    }
}
