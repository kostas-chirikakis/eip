using System.Diagnostics;
using CubeRM.Identity.Api.Contracts;
using CubeRM.Identity.Application.Routing.Queries;
using CubeRM.SharedKernel.Auth.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace CubeRM.Identity.Api.Endpoints;

/// <summary>
/// <c>POST /internal/token-issuance</c> — the Entra <c>OnTokenIssuanceStart</c> handler.
/// </summary>
/// <remarks>
/// <para>
/// Stamps <c>cube_org_id</c> into tokens, read from our database rather than from the
/// directory, so an organisation suspended in PostgreSQL cannot be issued a usable Cube RM
/// token even if directory state is stale.
/// </para>
/// <para>
/// <b>This is on the critical authentication path for every customer.</b> If it is slow or
/// down, nobody can sign in. Latency budget p99 &lt; 500 ms; hosted separately from product
/// APIs; lookups served from cache or a read replica, never the primary. The exact Entra
/// timeout is confirmed by risk register <b>R2</b>, along with the degraded mode
/// (directory-attribute-sourced claims) if the envelope proves unachievable.
/// </para>
/// </remarks>
public static class ClaimsEnrichmentEndpoint
{
    public static IEndpointRouteBuilder MapClaimsEnrichment(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/token-issuance", async (
                TokenIssuanceStartRequest request,
                IUserOrganizationResolver resolver,
                ILoggerFactory loggerFactory,
                CancellationToken ct) =>
            {
                var logger = loggerFactory.CreateLogger("ClaimsEnrichment");
                var stopwatch = Stopwatch.StartNew();

                var user = request.Data.AuthenticationContext.User;
                if (user is null || string.IsNullOrEmpty(user.Id))
                {
                    logger.LogWarning("Token issuance callout carried no user. Correlation {CorrelationId}.",
                        request.Data.AuthenticationContext.CorrelationId);
                    return Results.Ok(TokenIssuanceStartResponse.NoClaims());
                }

                var resolution = await resolver.ResolveAsync(
                    tenantId: request.Data.TenantId,
                    subjectObjectId: user.Id,
                    cancellationToken: ct);

                // Every failure path returns NoClaims(). The token is issued without
                // cube_org_id and is rejected everywhere downstream — fail closed, never
                // fail into a default organisation.
                switch (resolution.Outcome)
                {
                    case ResolutionOutcome.UserNotFound:
                        // Expected when just-in-time provisioning is off, which is the
                        // default: JIT + a shared tenant + a mis-pinned IdP is exactly the
                        // combination that silently creates a cross-organisation account.
                        logger.LogInformation("No user record for oid {Oid}. Issuing token without organisation claims.", user.Id);
                        return Results.Ok(TokenIssuanceStartResponse.NoClaims());

                    case ResolutionOutcome.OrganizationNotActive:
                        logger.LogWarning("Organisation {OrgSlug} is not active. Refusing to enrich.", resolution.OrgSlug);
                        return Results.Ok(TokenIssuanceStartResponse.NoClaims());

                    case ResolutionOutcome.IdentityProviderMismatch:
                        // The cross-organisation routing detector. This should never fire.
                        // If it does, treat it as a security incident, not a warning.
                        logger.LogCritical(
                            "Identity provider mismatch: user {Oid} of organisation {OrgSlug} authenticated via {ActualIdp}, " +
                            "which is not configured for that organisation. Possible cross-tenant routing.",
                            user.Id, resolution.OrgSlug, resolution.AuthenticationMethod);
                        return Results.Ok(TokenIssuanceStartResponse.NoClaims());

                    case ResolutionOutcome.Success:
                        logger.LogInformation(
                            "Enriched token for organisation {OrgSlug} in {ElapsedMs}ms.",
                            resolution.OrgSlug, stopwatch.ElapsedMilliseconds);

                        return Results.Ok(TokenIssuanceStartResponse.WithClaims(
                            new Dictionary<string, string>
                            {
                                [CubeClaimTypes.OrgId] = resolution.OrgId.ToString(),
                                [CubeClaimTypes.OrgSlug] = resolution.OrgSlug!,
                                [CubeClaimTypes.IdentityProvider] = resolution.AuthenticationMethod!,
                                [CubeClaimTypes.SchemaVersion] = CubeClaimTypes.CurrentSchemaVersion
                            }));

                    default:
                        return Results.Ok(TokenIssuanceStartResponse.NoClaims());
                }
            })
            // Entra authenticates to this endpoint with its own token. Anonymous access
            // would let anyone assert an organisation for any user.
            .RequireAuthorization("EntraCustomExtension")
            .WithName("TokenIssuanceStart");

        return app;
    }
}

public interface IUserOrganizationResolver
{
    Task<OrganizationResolution> ResolveAsync(string tenantId, string subjectObjectId, CancellationToken cancellationToken);
}

public sealed record OrganizationResolution(
    ResolutionOutcome Outcome,
    Guid OrgId = default,
    string? OrgSlug = null,
    string? AuthenticationMethod = null);

public enum ResolutionOutcome
{
    Success,
    UserNotFound,
    OrganizationNotActive,
    IdentityProviderMismatch
}
