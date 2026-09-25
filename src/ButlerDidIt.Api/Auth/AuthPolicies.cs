namespace ButlerDidIt.Api.Auth;

public static class AuthPolicies
{
    /// <summary>A signed-in host account (Identity cookie).</summary>
    public const string Host = "Host";

    /// <summary>Either a host (cookie) or a guest (seat token). Both schemes run, and their identities are combined.</summary>
    public const string PartyMember = "PartyMember";

    /// <summary>A guest with a seat token.</summary>
    public const string Seat = "Seat";
}
