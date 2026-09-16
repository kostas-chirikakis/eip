namespace CubeRM.Identity.Provisioning.Model;

/// <summary>
/// In-memory form of a <c>infra/customers/{slug}.yaml</c> declaration.
/// </summary>
/// <remarks>
/// Deserialised with YamlDotNet and validated against
/// <c>infra/customers/_schema/customer.schema.json</c> before anything is applied. The
/// schema is the contract; this type follows it.
/// </remarks>
public sealed record CustomerDeclaration(
    string ApiVersion,
    string Kind,
    CustomerMetadata Metadata,
    CustomerSpec Spec);

public sealed record CustomerMetadata(string Slug, string DisplayName);

public sealed record CustomerSpec(
    string Status,
    string Tier,
    IReadOnlyList<DomainDeclaration> Domains,
    AuthenticationDeclaration Authentication,
    RateLimitDeclaration? RateLimits,
    ContactsDeclaration Contacts);

public sealed record DomainDeclaration(string Domain, bool Verified, string? VerificationRef);

public sealed record AuthenticationDeclaration(
    string Mode,
    FederationDeclaration? Federation,
    TrustedIssuerDeclaration? TrustedIssuer,
    LocalAccountDeclaration? Local);

public sealed record FederationDeclaration(
    string Protocol,
    string DisplayName,
    string? MetadataUrl,
    string? EntityId,
    IReadOnlyDictionary<string, string> ClaimsMapping,
    JitProvisioningDeclaration JitProvisioning);

public sealed record JitProvisioningDeclaration(bool Enabled, bool RequireVerifiedDomain);

public sealed record TrustedIssuerDeclaration(
    string EntraTenantId,
    string IssuerUrl,
    IReadOnlyList<string> AllowedAudiences);

public sealed record LocalAccountDeclaration(bool SelfServiceSignUp, IReadOnlyList<string>? InitialAdmins);

public sealed record RateLimitDeclaration(int? CallsPerMinute, int? CallsPerDay);

public sealed record ContactsDeclaration(string SecurityContact, string TechnicalContact);

/// <summary>
/// Entra object naming. Centralised so the reconciler, the drift scanner and the orphan
/// detector all derive names the same way — divergence there means orphaned objects nobody
/// can attribute to a customer.
/// </summary>
public static class EntraNaming
{
    public static string IdentityProvider(string slug, string protocol) => $"idp-{slug}-{protocol}";
    public static string MembersGroup(string slug) => $"org.{slug}.members";
    public static string SuspendedGroup(string slug) => $"org.{slug}.suspended";
    public static string RateLimitNamedValue(string slug) => $"cube-ratelimit-{slug}";
}
