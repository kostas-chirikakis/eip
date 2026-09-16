namespace CubeRM.SharedKernel.Auth.Abstractions;

/// <summary>
/// In-memory, periodically refreshed view of which token issuers we trust and which
/// customer organisation each maps to.
/// </summary>
/// <remarks>
/// Backs both the Cube RM CIAM tenant and the per-customer Entra tenants on the
/// trusted-issuer path (ADR-0004). Lookups happen inside token validation, so every member
/// here is synchronous and must not perform I/O — refresh is a background timer.
/// </remarks>
public interface ITrustedIssuerRegistry
{
    /// <summary>True if the issuer is currently trusted and not suspended.</summary>
    bool IsTrusted(string issuer);

    /// <summary>
    /// Resolves an issuer to its organisation. Returns false for our own CIAM tenant, whose
    /// tokens carry <c>cube_org_id</c> directly, and for unknown or suspended issuers.
    /// </summary>
    bool TryResolveOrganization(string issuer, out TrustedIssuerOrganization organization);

    /// <summary>OIDC metadata address for an issuer, used to fetch its signing keys.</summary>
    bool TryGetMetadataAddress(string issuer, out string metadataAddress);

    /// <summary>Organisations currently suspended. Checked in-app as the authoritative deny-list.</summary>
    bool IsOrganizationSuspended(Guid orgId);

    /// <summary>Forces an immediate refresh. Called by the suspension flush endpoint.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed record TrustedIssuerOrganization(
    Guid OrgId,
    string OrgSlug,
    string EntraTenantId,
    IReadOnlyList<string> AllowedAudiences);
