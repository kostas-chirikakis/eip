namespace CubeRM.Identity.Domain.Routing;

/// <summary>
/// Maps an email domain to an organisation and its routing mode. The backing table for
/// home-realm discovery (ADR-0003).
/// </summary>
public sealed class OrganizationDomain
{
    /// <summary>Normalised: lowercase, trimmed, IDN-punycoded. Unique across ALL organisations.</summary>
    public string Domain { get; private init; } = string.Empty;

    public Guid OrgId { get; private init; }
    public RoutingMode Mode { get; private init; }

    /// <summary>Entra identity provider name, e.g. <c>idp-acme-pharma-saml</c>. Null for local routing.</summary>
    public string? IdentityProviderRef { get; private init; }

    /// <summary>
    /// DNS TXT ownership proof. Federation is never enabled for an unverified domain —
    /// otherwise one customer could claim another's domain and receive their sign-ins.
    /// </summary>
    public bool Verified { get; private init; }

    public string? VerificationRef { get; private init; }
    public DateTimeOffset? VerifiedAt { get; private init; }

    /// <summary>Federated routing requires verified ownership. Enforced here and in the onboarding schema.</summary>
    public bool CanRouteFederated => Verified && Mode is RoutingMode.Federated && IdentityProviderRef is not null;

    /// <summary>
    /// Normalises an email to its routable domain. Lowercases, takes the part after the
    /// last '@', and strips any trailing dot.
    /// </summary>
    public static string ExtractDomain(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1)
            throw new FormatException("Value is not an email address.");

        return email[(at + 1)..].Trim().TrimEnd('.').ToLowerInvariant();
    }
}

public enum RoutingMode
{
    Local,
    Federated,
    TrustedIssuer
}
