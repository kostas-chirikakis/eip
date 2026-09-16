namespace CubeRM.SharedKernel.Auth.Abstractions;

/// <summary>Thrown when a tenant-scoped operation runs without a resolved organisation.</summary>
public sealed class TenantContextMissingException(string requestName)
    : InvalidOperationException(
        $"'{requestName}' is tenant-scoped but no organisation was resolved for this scope. " +
        "HTTP requests resolve it from the validated token; background work must create an " +
        "explicit scope via ICubeTenantScopeFactory before touching data.")
{
    public string RequestName { get; } = requestName;
}

/// <summary>
/// Thrown when a request carries an explicit organisation that differs from the caller's.
/// This is a security event, not a validation error — it is logged and alerted as such.
/// </summary>
public sealed class CrossTenantAccessException(string requestName, Guid requestedOrgId, Guid callerOrgId)
    : UnauthorizedAccessException(
        $"'{requestName}' requested organisation {requestedOrgId} but the caller is scoped to {callerOrgId}.")
{
    public string RequestName { get; } = requestName;
    public Guid RequestedOrgId { get; } = requestedOrgId;
    public Guid CallerOrgId { get; } = callerOrgId;
}

/// <summary>Thrown when a token is well-formed and validly signed but cannot be mapped to an organisation.</summary>
public sealed class OrganizationResolutionException(string issuer, string reason)
    : UnauthorizedAccessException($"Could not resolve an organisation for issuer '{issuer}': {reason}")
{
    public string Issuer { get; } = issuer;
}
