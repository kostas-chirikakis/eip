using CubeRM.Identity.Domain.Organizations;
using CubeRM.Identity.Domain.Routing;

namespace CubeRM.Identity.Application.Routing.Queries;

/// <summary>An email domain together with the organisation it routes to.</summary>
public sealed record DomainRouting(OrganizationDomain Domain, Organization Organization);

public interface IOrganizationDomainRepository
{
    /// <summary>
    /// Resolves a normalised domain. On the home-realm-discovery hot path, so the
    /// implementation caches in-process with a short TTL — short enough that a suspension
    /// takes effect within the offboarding SLA.
    /// </summary>
    Task<DomainRouting?> FindByDomainAsync(string domain, CancellationToken cancellationToken);
}

/// <summary>
/// Builds Entra authorize URLs. Isolated behind an interface because the exact parameter
/// that reliably pins a single identity provider in an external tenant is risk register
/// R3 — unresolved until the spike lands. Owning home-realm discovery ourselves means only
/// this one implementation changes when the answer arrives.
/// </summary>
public interface IAuthorizeUrlBuilder
{
    string BuildLocal(string clientId);

    /// <param name="clientId">
    /// The single shared app registration. One registration serves every customer, which is
    /// what keeps onboarding customer #21 a data change rather than a portal exercise.
    /// </param>
    /// <param name="identityProviderRef">e.g. <c>idp-acme-pharma-saml</c>. Exactly one, never a list.</param>
    /// <param name="domainHint">
    /// The caller's verified email domain. A hint only: it steers the sign-in page but is
    /// not a security control, because the user can edit it. Isolation comes from the
    /// organisation on the resolved user record, never from this value.
    /// </param>
    string BuildFederated(string clientId, string identityProviderRef, string domainHint);

    /// <summary>Authorize URL against the customer's own Entra tenant (ADR-0004).</summary>
    string BuildTrustedIssuer(string clientId, Guid orgId);
}
