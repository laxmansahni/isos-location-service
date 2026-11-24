namespace ISOS.LocationService.Identity;

/// <summary>
/// The authenticated person (their party id, for example "IND_335dc59b-2b6a-4fa9-976f-d0c2b3d0647f") and,
/// once it can be resolved, their client, plus the function codes they hold (for example "MGRLOCHIS").
/// Always taken from the token, never from a request body.
/// </summary>
public sealed record CallerIdentity(
    string PartyId,
    string? ClientId = null,
    IReadOnlySet<string>? Permissions = null)
{
    public bool Has(string permission) => Permissions?.Contains(permission) ?? false;
}

/// <summary>Function codes that gate location reads (HLD 6, LLD 4.4).</summary>
public static class LocationPermissions
{
    /// <summary>Manager: view history, replay and the live map of other people.</summary>
    public const string ManagerHistory = "MGRLOCHIS";

    /// <summary>Member: view own history and replay.</summary>
    public const string UserHistory = "USRLOCHIS";
}

public interface ICallerContext
{
    /// <summary>The caller, or null when the request is not authenticated.</summary>
    CallerIdentity? Current { get; }
}

/// <summary>
/// Placeholder until token validation is built (HLD Q8). Reads the party id from the "partyId" header and is
/// registered in Development only. In any other environment every call is unauthenticated.
/// The client is not sent: it will be resolved from the party id on the server. The optional "permissions" header
/// (comma separated function codes) stands in for the codes that will come from the token.
/// </summary>
public sealed class DevelopmentHeaderCallerContext(IHttpContextAccessor accessor) : ICallerContext
{
    public const string PartyIdHeader = "partyId";
    public const string PermissionsHeader = "permissions";
    public const int MaxPartyIdLength = 100;

    public CallerIdentity? Current
    {
        get
        {
            var headers = accessor.HttpContext?.Request.Headers;
            var partyId = headers?[PartyIdHeader].ToString().Trim();
            if (string.IsNullOrEmpty(partyId) || partyId.Length > MaxPartyIdLength)
            {
                return null;
            }

            var permissions = headers![PermissionsHeader].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new CallerIdentity(partyId, Permissions: permissions);
        }
    }
}

public sealed class UnauthenticatedCallerContext : ICallerContext
{
    public CallerIdentity? Current => null;
}
