namespace CubeRM.SharedKernel.Auth.Claims;

/// <summary>
/// Claim names Cube RM injects via the <c>OnTokenIssuanceStart</c> custom authentication
/// extension, plus the standard claims we depend on.
/// </summary>
public static class CubeClaimTypes
{
    /// <summary>Customer organisation id (GUID). The only business-meaningful claim in the token.</summary>
    public const string OrgId = "cube_org_id";

    /// <summary>Immutable organisation slug. Correlation only — never an authorization input.</summary>
    public const string OrgSlug = "cube_org_slug";

    /// <summary><c>local</c> | <c>saml:{slug}</c> | <c>oidc:{slug}</c> | <c>entra:{tid}</c>.</summary>
    public const string IdentityProvider = "cube_idp";

    /// <summary>Claim schema version. Incremented when the shape changes, to support rollover.</summary>
    public const string SchemaVersion = "cube_ver";

    /// <summary>The claim schema version this build emits and understands.</summary>
    public const string CurrentSchemaVersion = "1";

    /// <summary>
    /// Stable object id within the issuing tenant. This — not <c>sub</c> — is the durable
    /// user key. <c>sub</c> is pairwise per application, so the same human presents a
    /// different <c>sub</c> to the web app and to the API.
    /// </summary>
    public const string ObjectId = "oid";

    /// <summary>Issuing tenant id.</summary>
    public const string TenantId = "tid";

    /// <summary>
    /// Roles are deliberately absent from tokens (ADR-0002). Present only so that a token
    /// arriving with roles can be detected and alerted on as a misconfiguration.
    /// </summary>
    public const string Roles = "roles";
}
