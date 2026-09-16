namespace CubeRM.SharedKernel.Auth.Abstractions;

/// <summary>
/// The single abstraction that CQRS handlers depend on to know who is calling and which
/// customer organisation they belong to.
/// </summary>
/// <remarks>
/// Handlers must depend on this and never on <see cref="System.Security.Claims.ClaimsPrincipal"/>,
/// <c>HttpContext</c>, or anything Entra-shaped. That keeps the Application layer free of
/// framework types and lets a future identity change land in one project.
/// </remarks>
public interface ICubeTenantContext
{
    /// <summary>True once an organisation has been established for this scope.</summary>
    bool IsResolved { get; }

    /// <summary>
    /// The customer organisation. Throws <see cref="TenantContextMissingException"/> when
    /// unresolved — deliberately, rather than returning <see cref="Guid.Empty"/>.
    /// A default-valued org id flowing into a query is the failure mode that produces a
    /// cross-tenant read, so it is made unrepresentable.
    /// </summary>
    Guid OrgId { get; }

    /// <summary>Immutable lowercase slug, e.g. <c>acme-pharma</c>. For logs and correlation.</summary>
    string OrgSlug { get; }

    /// <summary>Cube RM user id, resolved from <c>(issuer, subject_oid)</c>.</summary>
    Guid UserId { get; }

    /// <summary>The token issuer. Distinguishes our CIAM tenant from a trusted customer tenant.</summary>
    string Issuer { get; }

    /// <summary>How the user authenticated: <c>local</c>, <c>saml:{slug}</c>, <c>oidc:{slug}</c>, <c>entra:{tid}</c>.</summary>
    string AuthMethod { get; }

    /// <summary>True when acting under an audited Cube RM support impersonation grant.</summary>
    bool IsSupportSession { get; }

    /// <summary>Support ticket reference. Non-null exactly when <see cref="IsSupportSession"/> is true.</summary>
    string? SupportTicketRef { get; }
}

/// <summary>Mutable counterpart, set by the API layer and by background-work scope factories.</summary>
public interface ICubeTenantContextSetter
{
    void Set(CubeTenantContextValues values);
    void Clear();
}

public sealed record CubeTenantContextValues(
    Guid OrgId,
    string OrgSlug,
    Guid UserId,
    string Issuer,
    string AuthMethod,
    bool IsSupportSession = false,
    string? SupportTicketRef = null);
