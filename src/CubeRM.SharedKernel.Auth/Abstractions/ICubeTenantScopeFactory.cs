namespace CubeRM.SharedKernel.Auth.Abstractions;

/// <summary>
/// Creates an explicit tenant scope for work with no HTTP request behind it — Service Bus
/// consumers, Durable Functions activities, scheduled jobs.
/// </summary>
/// <remarks>
/// <para>
/// Background work is where tenant isolation is most often lost, because there is no token
/// to derive organisation from. The rule is that <c>OrgId</c> is a mandatory part of every
/// message and orchestration input, and the worker opens a scope from it before touching
/// any data (see §3.8).
/// </para>
/// <para>
/// A worker that forgets gets <see cref="TenantContextMissingException"/> from the pipeline
/// behaviour, and — if it somehow reaches the database anyway — zero rows from RLS. It
/// fails loudly rather than reading across organisations.
/// </para>
/// </remarks>
public interface ICubeTenantScopeFactory
{
    /// <param name="orgId">Organisation from the message. Never inferred or defaulted.</param>
    /// <param name="reason">Audited provenance, e.g. <c>service-bus:tender-indexing</c>.</param>
    ICubeTenantScope CreateScope(Guid orgId, string reason);

    /// <summary>Opens a scope for an audited support impersonation grant.</summary>
    ICubeTenantScope CreateSupportScope(Guid orgId, Guid supportUserId, string ticketRef);
}

public interface ICubeTenantScope : IDisposable
{
    IServiceProvider Services { get; }
    ICubeTenantContext Context { get; }
}
