using System.Security.Claims;
using CubeRM.SharedKernel.Auth.Abstractions;

namespace CubeRM.SharedKernel.Auth.Claims;

/// <summary>
/// Reads Cube RM claims off a validated principal.
/// </summary>
/// <remarks>
/// API-layer only. Application-layer code uses <see cref="ICubeTenantContext"/>; nothing in
/// Application or Domain should reference <see cref="ClaimsPrincipal"/> at all, and an
/// architecture test enforces that.
/// </remarks>
public static class ClaimsPrincipalExtensions
{
    public static string? GetIssuer(this ClaimsPrincipal principal) =>
        principal.FindFirst(CubeClaimTypes.ObjectId)?.Issuer
        ?? principal.FindFirst("iss")?.Value;

    /// <summary>
    /// The durable subject identifier within the issuing tenant. Prefers <c>oid</c> and
    /// falls back to <c>sub</c> only for issuers that do not emit <c>oid</c>.
    /// </summary>
    public static string? GetSubjectObjectId(this ClaimsPrincipal principal) =>
        principal.FindFirst(CubeClaimTypes.ObjectId)?.Value
        ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
        ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public static string? GetTenantId(this ClaimsPrincipal principal) =>
        principal.FindFirst(CubeClaimTypes.TenantId)?.Value
        ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value;

    /// <summary>
    /// Organisation id from the token, present only on tokens our CIAM tenant issued.
    /// Trusted-issuer tokens (ADR-0004) carry none — those resolve via
    /// <see cref="ITrustedIssuerRegistry"/> instead.
    /// </summary>
    public static Guid? GetOrgId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst(CubeClaimTypes.OrgId)?.Value, out var id) ? id : null;

    public static string? GetOrgSlug(this ClaimsPrincipal principal) =>
        principal.FindFirst(CubeClaimTypes.OrgSlug)?.Value;

    public static string GetAuthMethod(this ClaimsPrincipal principal) =>
        principal.FindFirst(CubeClaimTypes.IdentityProvider)?.Value ?? "unknown";

    public static string GetClaimSchemaVersion(this ClaimsPrincipal principal) =>
        principal.FindFirst(CubeClaimTypes.SchemaVersion)?.Value ?? "0";

    public static string? GetEmail(this ClaimsPrincipal principal) =>
        principal.FindFirst("email")?.Value
        ?? principal.FindFirst(ClaimTypes.Email)?.Value
        ?? principal.FindFirst("preferred_username")?.Value;

    /// <summary>
    /// True if the token carries role claims. Roles must never appear in Cube RM tokens
    /// (ADR-0002); if they do, something is misconfigured and it is worth an alert.
    /// </summary>
    public static bool HasUnexpectedRoleClaims(this ClaimsPrincipal principal) =>
        principal.FindFirst(CubeClaimTypes.Roles) is not null;
}
