namespace ISOS.LocationService.Identity;

/// <summary>
/// The authenticated person (their party id, for example "IND_335dc59b-2b6a-4fa9-976f-d0c2b3d0647f") and,
/// once it can be resolved, their client. Always taken from the token, never from a request body.
/// </summary>
public sealed record CallerIdentity(string PartyId, string? ClientId = null);

public interface ICallerContext
{
    /// <summary>The caller, or null when the request is not authenticated.</summary>
    CallerIdentity? Current { get; }
}

/// <summary>
/// Placeholder until token validation is built (HLD Q8). Reads the party id from the "partyId" header and is
/// registered in Development only. In any other environment every call is unauthenticated.
/// The client is not sent: it will be resolved from the party id on the server.
/// </summary>
public sealed class DevelopmentHeaderCallerContext(IHttpContextAccessor accessor) : ICallerContext
{
    public const string PartyIdHeader = "partyId";
    public const int MaxPartyIdLength = 100;

    public CallerIdentity? Current
    {
        get
        {
            var partyId = accessor.HttpContext?.Request.Headers[PartyIdHeader].ToString().Trim();
            return string.IsNullOrEmpty(partyId) || partyId.Length > MaxPartyIdLength
                ? null
                : new CallerIdentity(partyId);
        }
    }
}

public sealed class UnauthenticatedCallerContext : ICallerContext
{
    public CallerIdentity? Current => null;
}
