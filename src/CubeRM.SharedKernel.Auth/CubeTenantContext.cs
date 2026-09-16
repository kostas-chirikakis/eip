using CubeRM.SharedKernel.Auth.Abstractions;

namespace CubeRM.SharedKernel.Auth;

/// <summary>
/// Scoped implementation of <see cref="ICubeTenantContext"/>.
/// </summary>
/// <remarks>
/// Registered as <c>Scoped</c>, so it is per-HTTP-request and per-background-work-scope.
/// A DI scope is used rather than <c>AsyncLocal</c> because it composes correctly with
/// Service Bus and Durable Functions handlers, where there is no ambient HTTP context to
/// hang state from.
/// </remarks>
public sealed class CubeTenantContext : ICubeTenantContext, ICubeTenantContextSetter
{
    private CubeTenantContextValues? _values;

    public bool IsResolved => _values is not null;

    public Guid OrgId => Require().OrgId;
    public string OrgSlug => Require().OrgSlug;
    public Guid UserId => Require().UserId;
    public string Issuer => Require().Issuer;
    public string AuthMethod => Require().AuthMethod;
    public bool IsSupportSession => _values?.IsSupportSession ?? false;
    public string? SupportTicketRef => _values?.SupportTicketRef;

    public void Set(CubeTenantContextValues values)
    {
        // Re-assignment within a scope would mean an ambiguous org for anything already
        // executed in it, including a connection that has had its RLS variable set.
        if (_values is not null)
            throw new InvalidOperationException("Tenant context is already set for this scope and is immutable.");

        _values = values;
    }

    public void Clear() => _values = null;

    private CubeTenantContextValues Require() =>
        _values ?? throw new TenantContextMissingException("<scope>");
}
