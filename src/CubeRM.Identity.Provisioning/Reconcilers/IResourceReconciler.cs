using CubeRM.Identity.Provisioning.Model;

namespace CubeRM.Identity.Provisioning.Reconcilers;

/// <summary>
/// One reconciler per resource type. Each plans and applies independently, so a failure in
/// one does not leave the others in an unknown state.
/// </summary>
public interface IResourceReconciler
{
    string ResourceType { get; }

    /// <summary>Read-only. Must never write, so `plan` is always safe to run against production.</summary>
    Task<IReadOnlyList<PlannedChange>> PlanAsync(CustomerDeclaration declaration, CancellationToken ct);

    /// <summary>Applies the given changes. Idempotent — applying twice yields the same state.</summary>
    Task ApplyAsync(CustomerDeclaration declaration, IReadOnlyList<PlannedChange> changes, CancellationToken ct);
}

/// <summary>
/// Orchestrates the reconcilers in dependency order.
/// </summary>
/// <remarks>
/// Order matters: the organisation record must exist before domains can reference it, and
/// the identity provider must exist before a domain can route to it. Getting this wrong
/// produces a half-onboarded customer whose sign-in fails in a confusing way.
/// </remarks>
public sealed class ReconciliationOrchestrator(IReadOnlyList<IResourceReconciler> reconcilers)
{
    /// <summary>
    /// Dependency order. Deletions are applied in reverse, for the same reason.
    /// </summary>
    public static readonly string[] Order =
    [
        "Organization",          // our database — must exist first
        "IdentityProvider",      // Entra — domains reference it by name
        "TrustedIssuer",         // our database — ADR-0004 path
        "OperationalGroups",     // Entra — bulk operations and emergency disable
        "UserFlowAssociation",   // Entra — binds the IdP to the shared user flow
        "DomainRouting",         // our database — LAST, because it makes the customer live
        "RateLimitNamedValue"    // APIM
    ];

    public async Task<ReconciliationPlan> PlanAsync(
        CustomerDeclaration declaration, string targetTenant, CancellationToken ct)
    {
        var changes = new List<PlannedChange>();

        foreach (var resourceType in Order)
        {
            var reconciler = reconcilers.FirstOrDefault(r => r.ResourceType == resourceType);
            if (reconciler is null) continue;

            changes.AddRange(await reconciler.PlanAsync(declaration, ct));
        }

        return new ReconciliationPlan(declaration.Metadata.Slug, targetTenant, changes);
    }
}
