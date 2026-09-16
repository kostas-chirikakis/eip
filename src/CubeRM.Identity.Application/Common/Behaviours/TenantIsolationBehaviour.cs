using CubeRM.SharedKernel.Auth.Abstractions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CubeRM.Identity.Application.Common.Behaviours;

/// <summary>
/// L3 tenant isolation: every tenant-scoped request must have a resolved organisation, and
/// any explicit organisation on the request must match the caller's.
/// </summary>
/// <remarks>
/// <para>
/// Registered first in the MediatR pipeline, ahead of validation and logging, so an
/// isolation failure short-circuits before a handler can touch data.
/// </para>
/// <para>
/// This is the belt. PostgreSQL row-level security (L4) is the braces — see
/// <c>OrgScopedConnectionInterceptor</c>. Both exist because this one is code we wrote and
/// is therefore fallible.
/// </para>
/// </remarks>
public sealed class TenantIsolationBehaviour<TRequest, TResponse>(
    ICubeTenantContext tenant,
    ILogger<TenantIsolationBehaviour<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request is not ITenantScopedRequest scoped)
        {
            // Not tenant-scoped. ArchitectureTests assert such a request carries
            // [TenantExempt] with a justification, so this path is always deliberate.
            return await next();
        }

        if (!tenant.IsResolved)
        {
            logger.LogError(
                "Tenant-scoped request {RequestName} reached the pipeline with no organisation context.",
                typeof(TRequest).Name);
            throw new TenantContextMissingException(typeof(TRequest).Name);
        }

        // A request may carry an explicit organisation (admin and support operations do).
        // It must match the caller's. Guid.Empty means "use the caller's", which is the
        // ordinary case.
        if (scoped.OrgId != Guid.Empty && scoped.OrgId != tenant.OrgId)
        {
            // Security event, not a validation error. Logged critical and alerted.
            logger.LogCritical(
                "Cross-tenant access attempt. Request {RequestName} targeted organisation {RequestedOrgId} " +
                "but caller {UserId} is scoped to {CallerOrgId} via {AuthMethod}.",
                typeof(TRequest).Name, scoped.OrgId, tenant.UserId, tenant.OrgId, tenant.AuthMethod);

            throw new CrossTenantAccessException(typeof(TRequest).Name, scoped.OrgId, tenant.OrgId);
        }

        return await next();
    }
}
