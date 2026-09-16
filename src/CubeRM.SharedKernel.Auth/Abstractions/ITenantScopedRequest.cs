namespace CubeRM.SharedKernel.Auth.Abstractions;

/// <summary>
/// Marks a CQRS request as operating within a single customer organisation.
/// </summary>
/// <remarks>
/// <para>
/// Every <c>IRequest</c> in the solution must either implement this or carry
/// <see cref="TenantExemptAttribute"/>. An architecture test enforces that, so a new
/// handler that is neither fails the build rather than silently running unscoped.
/// </para>
/// <para>
/// <see cref="OrgId"/> is normally left as <see cref="Guid.Empty"/> and supplied from the
/// token by <c>TenantIsolationBehaviour</c>. When a request does carry an explicit org id —
/// for example a support or admin operation — the behaviour asserts it matches the token's
/// org and throws otherwise.
/// </para>
/// </remarks>
public interface ITenantScopedRequest
{
    Guid OrgId { get; }
}

/// <summary>
/// Exempts a request from tenant scoping. Requires a written justification, which the
/// architecture test surfaces in its failure output so exemptions stay visible in review.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class TenantExemptAttribute(string justification) : Attribute
{
    public string Justification { get; } = justification;
}
