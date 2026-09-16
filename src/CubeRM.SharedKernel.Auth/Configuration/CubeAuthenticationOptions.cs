using System.ComponentModel.DataAnnotations;

namespace CubeRM.SharedKernel.Auth.Configuration;

/// <summary>Bound from the <c>CubeAuthentication</c> configuration section.</summary>
public sealed class CubeAuthenticationOptions
{
    public const string SectionName = "CubeAuthentication";

    /// <summary>
    /// Authority of the Cube RM External ID tenant,
    /// e.g. <c>https://cuberm.ciamlogin.com/{tenantId}/v2.0</c>.
    /// </summary>
    [Required]
    public string CiamAuthority { get; init; } = string.Empty;

    /// <summary>Cube RM External ID tenant id.</summary>
    [Required]
    public string CiamTenantId { get; init; } = string.Empty;

    /// <summary>
    /// Audiences this API accepts, e.g. <c>api://cube-tenderhub</c>. Each Cube RM API has
    /// its own audience so a token minted for one is rejected by another.
    /// </summary>
    [Required, MinLength(1)]
    public string[] ValidAudiences { get; init; } = [];

    /// <summary>
    /// Cube RM workforce tenant authority, for support-engineer tokens
    /// (see §1.4.4). Support principals carry no organisation of their own.
    /// </summary>
    public string? WorkforceAuthority { get; init; }

    /// <summary>
    /// Clock skew allowance. Defaults to 30 seconds, not the framework's 5 minutes —
    /// the default silently extends every token's usable life past expiry and weakens the
    /// offboarding SLA.
    /// </summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the trusted-issuer and suspension registry refreshes. Kept at 30 seconds to
    /// match the APIM deny-list cache, so both revocation paths have the same time to effect.
    /// </summary>
    public TimeSpan IssuerRegistryRefreshInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Internal identity-service base address used to load the registry.</summary>
    [Required]
    public string IdentityServiceBaseAddress { get; init; } = string.Empty;

    /// <summary>
    /// Reject tokens whose claim schema version this build does not understand.
    /// Kept configurable so a version rollover can be staged.
    /// </summary>
    public bool RequireKnownClaimSchemaVersion { get; init; } = true;
}
