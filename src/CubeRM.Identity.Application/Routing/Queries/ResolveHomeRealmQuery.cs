using CubeRM.Identity.Domain.Routing;
using CubeRM.SharedKernel.Auth.Abstractions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CubeRM.Identity.Application.Routing.Queries;

/// <summary>
/// Resolves an email address to a sign-in route.
/// </summary>
/// <remarks>
/// Exempt from tenant scoping because it runs before any organisation is known — resolving
/// the organisation is precisely what it does.
/// </remarks>
[TenantExempt("Pre-authentication home-realm discovery: no organisation is established yet.")]
public sealed record ResolveHomeRealmQuery(string Email, string ClientId) : IRequest<HomeRealmResult>;

public sealed record HomeRealmResult(string Mode, string? AuthorizeUrl, string? Message);

public sealed class ResolveHomeRealmQueryHandler(
    IOrganizationDomainRepository domains,
    IAuthorizeUrlBuilder urlBuilder,
    ILogger<ResolveHomeRealmQueryHandler> logger)
    : IRequestHandler<ResolveHomeRealmQuery, HomeRealmResult>
{
    public async Task<HomeRealmResult> Handle(ResolveHomeRealmQuery request, CancellationToken ct)
    {
        string domain;
        try
        {
            domain = OrganizationDomain.ExtractDomain(request.Email);
        }
        catch (FormatException)
        {
            // Malformed input gets the same answer as an unknown domain. Distinguishing
            // them is a small oracle, and there is no benefit to the legitimate user.
            return LocalRoute(request.ClientId);
        }

        var routing = await domains.FindByDomainAsync(domain, ct);

        if (routing is null)
        {
            // Unknown domain. Identical response shape to a known local-account domain.
            logger.LogInformation("Home-realm discovery miss for domain {Domain}.", domain);
            return LocalRoute(request.ClientId);
        }

        if (!routing.Organization.CanAuthenticate)
        {
            // The kill switch. Evaluated from our own database, before Entra is contacted,
            // so suspension takes effect on the next sign-in with no directory change
            // (docs/identity/06-offboarding-incident.md §6.2, cut 2).
            logger.LogWarning(
                "Sign-in blocked for organisation {OrgSlug}: status {Status}.",
                routing.Organization.Slug, routing.Organization.Status);

            return new HomeRealmResult(
                Mode: "denied",
                AuthorizeUrl: null,
                Message: "Sign-in is unavailable for your organisation. Please contact your administrator.");
        }

        return routing.Domain.Mode switch
        {
            RoutingMode.Federated when routing.Domain.CanRouteFederated => new HomeRealmResult(
                Mode: "federated",
                // Pins exactly one identity provider. The user never sees a picker listing
                // every customer's IdP (docs/identity/01-tenant-isolation-model.md §1.5.2).
                AuthorizeUrl: urlBuilder.BuildFederated(
                    request.ClientId, routing.Domain.IdentityProviderRef!, domain),
                Message: null),

            RoutingMode.Federated => HandleUnverifiedFederation(routing, request.ClientId),

            RoutingMode.TrustedIssuer => new HomeRealmResult(
                Mode: "trusted-issuer",
                // Authenticates against the customer's own Entra tenant (ADR-0004).
                AuthorizeUrl: urlBuilder.BuildTrustedIssuer(request.ClientId, routing.Organization.Id),
                Message: null),

            _ => LocalRoute(request.ClientId)
        };
    }

    private HomeRealmResult HandleUnverifiedFederation(DomainRouting routing, string clientId)
    {
        // Configuration error: federation declared but domain ownership unproven. Routing
        // it anyway would let one customer harvest another's sign-ins, so fall back to
        // local and alert loudly. CI's onboarding checks should make this unreachable.
        logger.LogError(
            "Organisation {OrgSlug} is configured for federated routing on an unverified domain. " +
            "Falling back to local. This should have been caught at onboarding.",
            routing.Organization.Slug);

        return LocalRoute(clientId);
    }

    /// <summary>
    /// The local-account route, and also the response for an unknown or malformed domain.
    /// Every caller gets the same shape so the endpoint cannot be used to enumerate
    /// customers.
    /// </summary>
    private HomeRealmResult LocalRoute(string clientId) =>
        new("local", urlBuilder.BuildLocal(clientId), null);
}
