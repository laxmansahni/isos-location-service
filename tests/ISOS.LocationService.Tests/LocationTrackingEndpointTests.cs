using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ISOS.LocationService.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ISOS.LocationService.Tests;

public class LocationTrackingEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Route = "/v1/locationtracking/points";

    private const string PartyA = "IND_335dc59b-2b6a-4fa9-976f-d0c2b3d0647f";
    private const string PartyB = "IND_af1418f5-8130-42f4-9c57-5883169386b6";

    private readonly WebApplicationFactory<Program> _factory;

    public LocationTrackingEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = SharingTestSetup.Configure(factory);
        Seed(PartyA);
        Seed(PartyB);
    }

    // Live points need consent and a chosen type. The in-memory store completes synchronously.
    private void Seed(string partyId) =>
        SharingTestSetup.EnableAsync(_factory.Services, partyId).GetAwaiter().GetResult();

    private static object Point(Guid? pointId = null, DateTimeOffset? capturedAt = null, double latitude = 51.5074) => new
    {
        pointId = pointId ?? Guid.NewGuid(),
        capturedAt = capturedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
        utcOffsetMinutes = 60,
        latitude,
        longitude = -0.1276,
        accuracy = 12.5,
        speed = 1.4,
        course = 270,
        shared = true,
    };

    private static object Body(params object[] points) => new
    {
        points,
        device = new { id = "device-1", platform = "ios", appVersion = "1.0.0", timezone = "Europe/London" },
    };

    private static HttpRequestMessage Request(object body, string? partyId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route) { Content = JsonContent.Create(body) };
        if (partyId is not null)
        {
            request.Headers.Add(DevelopmentHeaderCallerContext.PartyIdHeader, partyId);
            request.Headers.Add(DevelopmentHeaderCallerContext.ClientIdHeader, SharingTestSetup.ClientId);
        }

        return request;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>());

    [Fact]
    public async Task Valid_point_is_stored()
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(Request(Body(Point()), PartyA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadAsync(response);
        Assert.Equal(1, json.GetProperty("stored").GetInt32());
        Assert.Equal("Stored", json.GetProperty("results")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Resending_the_same_point_is_reported_as_duplicate()
    {
        var client = _factory.CreateClient();
        var body = Body(Point(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-2)));

        await client.SendAsync(Request(body, PartyA));
        var second = await ReadAsync(await client.SendAsync(Request(body, PartyA)));

        Assert.Equal(0, second.GetProperty("stored").GetInt32());
        Assert.Equal(1, second.GetProperty("duplicates").GetInt32());
        Assert.Equal("Duplicate", second.GetProperty("results")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_same_point_id_from_another_party_is_not_a_duplicate()
    {
        var client = _factory.CreateClient();
        var body = Body(Point(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-3)));

        await client.SendAsync(Request(body, PartyA));
        var other = await ReadAsync(await client.SendAsync(Request(body, PartyB)));

        Assert.Equal(1, other.GetProperty("stored").GetInt32());
    }

    [Fact]
    public async Task Invalid_points_are_rejected_individually_without_failing_the_batch()
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(Request(Body(Point(), Point(latitude: 123), Point()), PartyA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadAsync(response);
        Assert.Equal(2, json.GetProperty("stored").GetInt32());
        Assert.Equal(1, json.GetProperty("rejected").GetInt32());
        Assert.Equal("INVALID_COORDINATES", json.GetProperty("results")[1].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Party_and_client_in_the_body_are_ignored()
    {
        var client = _factory.CreateClient();
        var point = Point(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-4));
        var device = new { id = "device-1" };
        var withForgedIdentity = new { partyId = PartyB, userId = PartyB, clientId = "other-client", points = new[] { point }, device };
        var plain = new { points = new[] { point }, device };

        // Stored under the caller (PartyA), not the PartyB named in the body.
        await client.SendAsync(Request(withForgedIdentity, PartyA));

        // So the identical point is a duplicate for PartyA but new for PartyB.
        var asA = await ReadAsync(await client.SendAsync(Request(plain, PartyA)));
        var asB = await ReadAsync(await client.SendAsync(Request(plain, PartyB)));
        Assert.Equal(1, asA.GetProperty("duplicates").GetInt32());
        Assert.Equal(1, asB.GetProperty("stored").GetInt32());
    }

    [Fact]
    public async Task Request_without_party_id_is_unauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(Request(Body(Point())));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Party_id_with_a_prefix_is_accepted_as_is()
    {
        var client = _factory.CreateClient();
        Seed("IND_92664bb1-eabf-4c47-af84-08f1cdb7a8f8");

        var response = await client.SendAsync(Request(Body(Point()), "IND_92664bb1-eabf-4c47-af84-08f1cdb7a8f8"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Overlong_party_id_is_unauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(Request(Body(Point()), new string('x', 101)));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Point_without_the_sharing_flag_is_rejected_not_assumed_shared()
    {
        var client = _factory.CreateClient();
        var withoutFlag = new
        {
            pointId = Guid.NewGuid(),
            capturedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            latitude = 51.5074,
            longitude = -0.1276,
            accuracy = 12.5,
        };

        var response = await client.SendAsync(Request(Body(withoutFlag), PartyA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadAsync(response);
        Assert.Equal(1, json.GetProperty("rejected").GetInt32());
        Assert.Equal("MISSING_FIELD", json.GetProperty("results")[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Not_shared_point_is_accepted()
    {
        var client = _factory.CreateClient();
        var notShared = new
        {
            pointId = Guid.NewGuid(),
            capturedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            latitude = 51.5074,
            longitude = -0.1276,
            accuracy = 12.5,
            shared = false,
        };

        var response = await client.SendAsync(Request(Body(notShared), PartyA));

        Assert.Equal("Stored", (await ReadAsync(response)).GetProperty("results")[0].GetProperty("status").GetString());
    }

    private static object NotSharedPoint(Guid pointId, DateTimeOffset capturedAt) => new
    {
        pointId,
        capturedAt,
        latitude = 51.5074,
        longitude = -0.1276,
        accuracy = 12.5,
        shared = false,
    };

    [Fact]
    public async Task Not_shared_points_report_stored_then_duplicate_then_superseded()
    {
        var client = _factory.CreateClient();
        const string party = "IND_not-shared-flow";
        var now = DateTimeOffset.UtcNow;
        var newest = NotSharedPoint(Guid.NewGuid(), now.AddMinutes(-5));
        var older = NotSharedPoint(Guid.NewGuid(), now.AddMinutes(-20));

        var first = await ReadAsync(await client.SendAsync(Request(Body(newest), party)));
        var retry = await ReadAsync(await client.SendAsync(Request(Body(newest), party)));
        var stale = await ReadAsync(await client.SendAsync(Request(Body(older), party)));

        Assert.Equal("Stored", first.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal("Duplicate", retry.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal("Superseded", stale.GetProperty("results")[0].GetProperty("status").GetString());
        Assert.Equal(1, stale.GetProperty("superseded").GetInt32());
        Assert.Equal(0, stale.GetProperty("stored").GetInt32());
    }

    [Fact]
    public async Task Mixed_batch_reports_every_status_in_one_response()
    {
        var client = _factory.CreateClient();
        const string party = "IND_mixed-batch";
        Seed(party);
        var now = DateTimeOffset.UtcNow;
        var latest = NotSharedPoint(Guid.NewGuid(), now.AddMinutes(-2));
        var older = NotSharedPoint(Guid.NewGuid(), now.AddMinutes(-9));
        var invalid = new { pointId = Guid.NewGuid(), capturedAt = now.AddMinutes(-3), latitude = 999, longitude = 0, accuracy = 5, shared = true };
        await client.SendAsync(Request(Body(latest), party));

        var json = await ReadAsync(await client.SendAsync(Request(Body(latest, older, invalid, Point()), party)));

        Assert.Equal(1, json.GetProperty("stored").GetInt32());
        Assert.Equal(1, json.GetProperty("duplicates").GetInt32());
        Assert.Equal(1, json.GetProperty("superseded").GetInt32());
        Assert.Equal(1, json.GetProperty("rejected").GetInt32());
    }

    [Fact]
    public async Task Empty_batch_is_bad_request()
    {
        var client = _factory.CreateClient();

        var response = await client.SendAsync(Request(Body(), PartyA));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Batch_over_the_limit_is_bad_request()
    {
        var client = _factory.CreateClient();
        var points = Enumerable.Range(0, 101).Select(_ => Point()).ToArray();

        var response = await client.SendAsync(Request(Body(points), PartyA));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Batch_of_exactly_one_hundred_is_accepted()
    {
        var client = _factory.CreateClient();
        var points = Enumerable.Range(0, 100).Select(_ => Point()).ToArray();

        var response = await client.SendAsync(Request(Body(points), PartyA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(100, (await ReadAsync(response)).GetProperty("stored").GetInt32());
    }

    [Fact]
    public async Task Missing_device_id_is_bad_request()
    {
        var client = _factory.CreateClient();
        var body = new { points = new[] { Point() }, device = new { platform = "ios" } };

        var response = await client.SendAsync(Request(body, PartyA));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
