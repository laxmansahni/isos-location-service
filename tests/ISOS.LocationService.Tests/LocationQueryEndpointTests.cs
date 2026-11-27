using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ISOS.LocationService.Audit;
using ISOS.LocationService.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ISOS.LocationService.Tests;

public class LocationQueryEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Base = "/v1/locationtracking";
    private const string Manager = "IND_manager";

    private readonly WebApplicationFactory<Program> _factory;

    public LocationQueryEndpointTests(WebApplicationFactory<Program> factory) =>
        _factory = SharingTestSetup.Configure(factory);

    private static string NewParty() => $"IND_{Guid.NewGuid()}";

    private static HttpRequestMessage Get(string url, string? partyId, string? permissions = null, string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (partyId is not null)
        {
            request.Headers.Add(DevelopmentHeaderCallerContext.PartyIdHeader, partyId);
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

    private async Task SendPointAsync(string partyId, DateTimeOffset capturedAt, bool shared = true, double latitude = 51.5)
    {
        var body = new
        {
            points = new[]
            {
                new { pointId = Guid.NewGuid(), capturedAt, utcOffsetMinutes = 60, latitude, longitude = -0.12, accuracy = 10.0, speed = 1.4, course = 270, shared },
            },
            device = new { id = "device-1", platform = "ios" },
        };
        await SharingTestSetup.EnableAsync(_factory.Services, partyId);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/points") { Content = JsonContent.Create(body) };
        request.Headers.Add(DevelopmentHeaderCallerContext.PartyIdHeader, partyId);
        request.Headers.Add(DevelopmentHeaderCallerContext.ClientIdHeader, SharingTestSetup.ClientId);
        var response = await _factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> ReadAsync(HttpRequestMessage request)
    {
        var response = await _factory.CreateClient().SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement);
    }

    [Fact]
    public async Task Queries_without_a_caller_are_unauthorized()
    {
        foreach (var url in new[] { $"{Base}/latest", $"{Base}/users/x/history", $"{Base}/users/x/replay" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await ReadAsync(Get(url, null))).Status);
        }
    }

    [Fact]
    public async Task Own_history_is_newest_first_and_needs_USRLOCHIS()
    {
        var party = NewParty();
        var now = DateTimeOffset.UtcNow;
        await SendPointAsync(party, now.AddMinutes(-30), latitude: 10);
        await SendPointAsync(party, now.AddMinutes(-10), latitude: 20);

        var denied = await ReadAsync(Get($"{Base}/users/{party}/history", party));
        var (status, body) = await ReadAsync(Get($"{Base}/users/{party}/history", party, "USRLOCHIS"));

        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("HISTORY_NOT_PERMITTED", denied.Body.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, status);
        var points = body.GetProperty("points");
        Assert.Equal(2, points.GetArrayLength());
        Assert.Equal(20, points[0].GetProperty("latitude").GetDouble());
        Assert.Equal("LOCATION_SHARE", points[0].GetProperty("type").GetString());
        Assert.Equal(1.4, points[0].GetProperty("speedMps").GetDouble());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task History_excludes_points_outside_the_range_and_not_shared_points()
    {
        var party = NewParty();
        var now = DateTimeOffset.UtcNow;
        await SendPointAsync(party, now.AddHours(-3));
        await SendPointAsync(party, now.AddMinutes(-5), shared: false);

        var (_, oneHour) = await ReadAsync(Get($"{Base}/users/{party}/history", party, "USRLOCHIS"));
        var (_, day) = await ReadAsync(Get($"{Base}/users/{party}/history?range=24h", party, "USRLOCHIS"));

        Assert.Equal(0, oneHour.GetProperty("points").GetArrayLength());
        Assert.Equal(1, day.GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task History_pages_with_a_cursor()
    {
        var party = NewParty();
        var now = DateTimeOffset.UtcNow;
        for (var i = 1; i <= 3; i++)
        {
            await SendPointAsync(party, now.AddMinutes(-i));
        }

        var (_, first) = await ReadAsync(Get($"{Base}/users/{party}/history?limit=2", party, "USRLOCHIS"));
        var cursor = first.GetProperty("nextCursor").GetString();
        var (_, second) = await ReadAsync(Get($"{Base}/users/{party}/history?limit=2&cursor={cursor}", party, "USRLOCHIS"));

        Assert.Equal(2, first.GetProperty("points").GetArrayLength());
        Assert.Equal(1, second.GetProperty("points").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Manager_reading_another_person_is_audited_without_coordinates()
    {
        var target = NewParty();
        await SendPointAsync(target, DateTimeOffset.UtcNow.AddMinutes(-5));

        var forbidden = await ReadAsync(Get($"{Base}/users/{target}/history", Manager, "USRLOCHIS"));
        var history = await ReadAsync(Get($"{Base}/users/{target}/history?range=24h", Manager, "MGRLOCHIS"));
        var replay = await ReadAsync(Get($"{Base}/users/{target}/replay", Manager, "MGRLOCHIS"));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.Status);
        Assert.Equal(HttpStatusCode.OK, history.Status);
        Assert.Equal(HttpStatusCode.OK, replay.Status);
        var entries = _factory.Services.GetRequiredService<InMemoryAccessAuditStore>().Entries
            .Where(e => e.TargetPartyId == target).ToList();
        Assert.Equal(["HISTORY", "REPLAY"], entries.Select(e => e.DataType));
        Assert.All(entries, e => Assert.Equal(Manager, e.ActorPartyId));
        Assert.Equal("24h", entries[0].Range);
    }

    [Fact]
    public async Task Reading_own_history_is_not_audited()
    {
        var party = NewParty();

        await ReadAsync(Get($"{Base}/users/{party}/history", party, "USRLOCHIS"));

        Assert.DoesNotContain(_factory.Services.GetRequiredService<InMemoryAccessAuditStore>().Entries, e => e.TargetPartyId == party);
    }

    [Fact]
    public async Task Empty_history_and_replay_are_200_with_no_points()
    {
        var party = NewParty();

        var history = await ReadAsync(Get($"{Base}/users/{party}/history", party, "USRLOCHIS"));
        var replay = await ReadAsync(Get($"{Base}/users/{party}/replay", party, "USRLOCHIS"));

        Assert.Equal(0, history.Body.GetProperty("points").GetArrayLength());
        Assert.Equal(0, replay.Body.GetProperty("points").GetArrayLength());
        Assert.False(replay.Body.GetProperty("partial").GetBoolean());
    }

    [Fact]
    public async Task Replay_is_oldest_first()
    {
        var party = NewParty();
        var now = DateTimeOffset.UtcNow;
        await SendPointAsync(party, now.AddMinutes(-10), latitude: 20);
        await SendPointAsync(party, now.AddMinutes(-30), latitude: 10);

        var (_, body) = await ReadAsync(Get($"{Base}/users/{party}/replay?range=24h", party, "USRLOCHIS"));

        var points = body.GetProperty("points");
        Assert.Equal(10, points[0].GetProperty("latitude").GetDouble());
        Assert.Equal(20, points[1].GetProperty("latitude").GetDouble());
    }

    [Theory]
    [InlineData("range=2h")]
    [InlineData("limit=0")]
    [InlineData("cursor=garbage")]
    public async Task Invalid_query_is_400(string query)
    {
        var party = NewParty();

        Assert.Equal(HttpStatusCode.BadRequest, (await ReadAsync(Get($"{Base}/users/{party}/history?{query}", party, "USRLOCHIS"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await ReadAsync(Get($"{Base}/users/{party}/replay?{query}", party, "USRLOCHIS"))).Status);
    }

    [Fact]
    public async Task Latest_needs_the_manager_code()
    {
        var result = await ReadAsync(Get($"{Base}/latest", Manager, "USRLOCHIS"));

        Assert.Equal(HttpStatusCode.Forbidden, result.Status);
    }

    [Fact]
    public async Task Latest_shows_only_fresh_shared_people()
    {
        var shared = NewParty();
        var notShared = NewParty();
        var stale = NewParty();
        var now = DateTimeOffset.UtcNow;
        await SendPointAsync(shared, now.AddMinutes(-2), latitude: 30);
        await SendPointAsync(notShared, now.AddMinutes(-1), shared: false);
        await SendPointAsync(stale, now.AddHours(-14));

        var (status, body) = await ReadAsync(Get($"{Base}/latest", Manager, "MGRLOCHIS"));

        Assert.Equal(HttpStatusCode.OK, status);
        var users = body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("userId").GetString()).ToList();
        Assert.Contains(shared, users);
        Assert.DoesNotContain(notShared, users);
        Assert.DoesNotContain(stale, users);
        var item = body.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("userId").GetString() == shared);
        Assert.InRange(item.GetProperty("freshnessSeconds").GetInt32(), 110, 300);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("firstName").ValueKind);
    }

    [Fact]
    public async Task A_newer_not_shared_point_removes_the_person_from_the_map()
    {
        var party = NewParty();
        var now = DateTimeOffset.UtcNow;
        await SendPointAsync(party, now.AddMinutes(-5));
        await SendPointAsync(party, now.AddMinutes(-1), shared: false);

        var (_, body) = await ReadAsync(Get($"{Base}/latest", Manager, "MGRLOCHIS"));

        Assert.DoesNotContain(body.GetProperty("items").EnumerateArray(), i => i.GetProperty("userId").GetString() == party);
    }

    [Fact]
    public async Task Latest_supports_etag_and_since()
    {
        var party = NewParty();
        await SendPointAsync(party, DateTimeOffset.UtcNow.AddMinutes(-1));
        var client = _factory.CreateClient();

        var first = await client.SendAsync(Get($"{Base}/latest", Manager, "MGRLOCHIS"));
        var etag = first.Headers.ETag!.Tag;
        var second = await client.SendAsync(Get($"{Base}/latest", Manager, "MGRLOCHIS", etag));
        var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;
        var nextSince = body.GetProperty("nextSince").GetDateTimeOffset();
        var delta = await ReadAsync(Get($"{Base}/latest?since={Uri.EscapeDataString(nextSince.ToString("O"))}", Manager, "MGRLOCHIS"));

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
        Assert.Equal(0, delta.Body.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Latest_rejects_an_unknown_type_and_returns_nothing_when_shares_are_not_requested()
    {
        var party = NewParty();
        await SendPointAsync(party, DateTimeOffset.UtcNow.AddMinutes(-1));

        var bad = await ReadAsync(Get($"{Base}/latest?types=NOPE", Manager, "MGRLOCHIS"));
        var checkInsOnly = await ReadAsync(Get($"{Base}/latest?types=CHECK_IN", Manager, "MGRLOCHIS"));

        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal(0, checkInsOnly.Body.GetProperty("items").GetArrayLength());
    }
}
