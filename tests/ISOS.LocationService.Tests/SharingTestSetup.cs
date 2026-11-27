using ISOS.LocationService.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ISOS.LocationService.Tests;

/// <summary>
/// Live-sharing points are only accepted for a person who has consented and chosen a type, so endpoint tests
/// grant both up front. Consent is dated far in the past so points captured earlier in a test are still allowed.
/// </summary>
internal static class SharingTestSetup
{
    public const string ClientId = "client-1";

    /// <summary>The "Testing" environment, with live sharing enabled for clients that have no configuration of their own.</summary>
    public static WebApplicationFactory<Program> Configure(WebApplicationFactory<Program> factory, Action<IWebHostBuilder>? extra = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            // "Testing" loads neither user secrets nor appsettings.Local.json, and uses the in-memory store.
            builder.UseEnvironment("Testing");
            builder.UseSetting("Policy:DefaultClient:SharingEnabled", "true");
            extra?.Invoke(builder);
        });

    public static async Task EnableAsync(
        IServiceProvider services, string partyId, string primaryType = SharingTypes.Always, string clientId = ClientId)
    {
        var store = services.GetRequiredService<IPolicyStore>();
        var longAgo = DateTimeOffset.UtcNow.AddDays(-30);
        await store.GrantConsentAsync(partyId, clientId, "1", longAgo, CancellationToken.None);
        await store.SavePreferenceAsync(partyId, clientId, new SharingPreference(primaryType, null, []), longAgo, CancellationToken.None);
    }
}
