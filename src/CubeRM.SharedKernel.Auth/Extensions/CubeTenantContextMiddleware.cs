using CubeRM.SharedKernel.Auth.Abstractions;
using CubeRM.SharedKernel.Auth.Claims;
using CubeRM.SharedKernel.Auth.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CubeRM.SharedKernel.Auth.Extensions;

/// <summary>
/// Populates <see cref="ICubeTenantContext"/> from the validated principal.
/// </summary>
/// <remarks>
/// Must run after <c>UseAuthentication</c>. Handles both token shapes: our CIAM tokens
/// carry <c>cube_org_id</c> directly; trusted-issuer tokens (ADR-0004) carry none and are
/// resolved server-side from <see cref="ITrustedIssuerRegistry"/>.
/// </remarks>
public sealed class CubeTenantContextMiddleware(
    RequestDelegate next,
    ITrustedIssuerRegistry registry,
    IOptions<CubeAuthenticationOptions> options,
    ILogger<CubeTenantContextMiddleware> logger)
{
    private readonly CubeAuthenticationOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context, ICubeTenantContextSetter setter)
    {
        var principal = context.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var issuer = principal.GetIssuer();
        var subjectOid = principal.GetSubjectObjectId();

        if (string.IsNullOrEmpty(issuer) || string.IsNullOrEmpty(subjectOid))
        {
            await Reject(context, "token_missing_subject");
            return;
        }

        if (principal.HasUnexpectedRoleClaims())
        {
            // Roles must never appear in Cube RM tokens (ADR-0002). Their presence means a
            // misconfigured app registration, which is worth knowing about immediately.
            logger.LogWarning(
                "Token from issuer {Issuer} carries role claims, which Cube RM tokens never should. " +
                "Check the app registration for stray app-role assignments.", issuer);
        }

        Guid orgId;
        string orgSlug;
        string authMethod;

        if (principal.GetOrgId() is { } claimOrgId)
        {
            // Path A — our CIAM tenant. Claims enriched at issuance from our database.
            orgId = claimOrgId;
            orgSlug = principal.GetOrgSlug() ?? string.Empty;
            authMethod = principal.GetAuthMethod();

            if (_options.RequireKnownClaimSchemaVersion &&
                principal.GetClaimSchemaVersion() != CubeClaimTypes.CurrentSchemaVersion)
            {
                await Reject(context, "unsupported_claim_schema_version");
                return;
            }
        }
        else if (registry.TryResolveOrganization(issuer, out var trusted))
        {
            // Path B — customer's own Entra tenant. Organisation comes from the registry,
            // where issuer -> org is one-to-one and enforced by a unique constraint. It is
            // never inferred from a token claim on this path.
            orgId = trusted.OrgId;
            orgSlug = trusted.OrgSlug;
            authMethod = $"entra:{trusted.EntraTenantId}";
        }
        else
        {
            // A validly signed token we cannot map to an organisation. This is the
            // fail-closed outcome from §2.3 and §2.6 — for instance a federated sign-in
            // with no provisioned user record.
            logger.LogWarning("Validly signed token from {Issuer} could not be mapped to an organisation.", issuer);
            await Reject(context, "organization_unresolved");
            return;
        }

        // Authoritative deny-list check. APIM checks this too, but the gateway is a gate,
        // never the decision (§3.4).
        if (registry.IsOrganizationSuspended(orgId))
        {
            await Reject(context, "organization_suspended", StatusCodes.Status403Forbidden);
            return;
        }

        // Tripwire: APIM sets X-Cube-Org-Id from the claims it validated. A mismatch means a
        // gateway bug or a bypass attempt. The header is never an input — orgId above came
        // from the token, not from here.
        var gatewayOrgId = context.Request.Headers["X-Cube-Org-Id"].ToString();
        if (!string.IsNullOrEmpty(gatewayOrgId) &&
            !string.Equals(gatewayOrgId, orgId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            logger.LogCritical(
                "Gateway/token organisation mismatch. Header {HeaderOrgId}, token {TokenOrgId}. " +
                "Treat as a potential gateway bypass.", gatewayOrgId, orgId);
            await Reject(context, "organization_mismatch", StatusCodes.Status403Forbidden);
            return;
        }

        setter.Set(new CubeTenantContextValues(
            OrgId: orgId,
            OrgSlug: orgSlug,
            UserId: ResolveCubeUserId(issuer, subjectOid),
            Issuer: issuer,
            AuthMethod: authMethod));

        await next(context);
    }

    /// <summary>
    /// Deterministic Cube RM user id from <c>(issuer, subject_oid)</c>.
    /// </summary>
    /// <remarks>
    /// The composite key matters: an <c>oid</c> from a customer's own tenant is unique only
    /// within that tenant, so <c>oid</c> alone would collide across issuers.
    /// Replace this placeholder with a lookup against <c>identity.users</c> (cached) — the
    /// signature is deliberately the same either way.
    /// </remarks>
    private static Guid ResolveCubeUserId(string issuer, string subjectOid) =>
        throw new NotImplementedException(
            "Wire to IUserDirectory.ResolveAsync(issuer, subjectOid) backed by identity.users. " +
            "See docs/identity/03-dotnet-integration.md §3.6.");

    private static async Task Reject(HttpContext context, string error, int statusCode = StatusCodes.Status401Unauthorized)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        // Deliberately terse. Detailed auth failures are a reconnaissance aid.
        await context.Response.WriteAsync($"{{\"error\":\"{error}\"}}");
    }
}
