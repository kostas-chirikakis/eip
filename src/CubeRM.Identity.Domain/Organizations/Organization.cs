namespace CubeRM.Identity.Domain.Organizations;

/// <summary>A customer organisation. The unit of isolation throughout the platform.</summary>
public sealed class Organization
{
    public Guid Id { get; private init; }

    /// <summary>Immutable. Embedded in Entra object names, so renaming would require directory churn across the shared tenant.</summary>
    public string Slug { get; private init; } = string.Empty;

    /// <summary>Mutable. UI only, never an identifier. A rebrand changes this and nothing else.</summary>
    public string DisplayName { get; private set; } = string.Empty;

    public OrganizationStatus Status { get; private set; }
    public AuthenticationMode AuthenticationMode { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset? SuspendedAt { get; private set; }
    public string? SuspensionReason { get; private set; }

    /// <summary>
    /// True when this organisation may authenticate. Evaluated at home-realm discovery and
    /// again at token issuance, so suspension takes effect without any Entra change.
    /// </summary>
    public bool CanAuthenticate => Status is OrganizationStatus.Active or OrganizationStatus.Offboarding;

    /// <summary>Offboarding organisations can still sign in but cannot write — a read-only wind-down period.</summary>
    public bool CanWrite => Status is OrganizationStatus.Active;

    public void Suspend(string reason, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A suspension reason is required for the audit trail.", nameof(reason));

        Status = OrganizationStatus.Suspended;
        SuspendedAt = at;
        SuspensionReason = reason;
    }

    public void Reinstate()
    {
        if (Status is not OrganizationStatus.Suspended)
            throw new InvalidOperationException($"Only a suspended organisation can be reinstated. Current status: {Status}.");

        Status = OrganizationStatus.Active;
        SuspendedAt = null;
        SuspensionReason = null;
    }
}

public enum OrganizationStatus
{
    Active,
    /// <summary>Fully reversible emergency cut-off. See docs/identity/06-offboarding-incident.md §6.3.</summary>
    Suspended,
    /// <summary>Contractual wind-down. Sign-in still works, writes are rejected.</summary>
    Offboarding,
    /// <summary>
    /// Access fully revoked. The row and its data persist under RLS — Life Sciences GxP
    /// retention frequently outlives the contract, so access revocation and data deletion
    /// are deliberately decoupled (§6.4).
    /// </summary>
    Offboarded
}

public enum AuthenticationMode
{
    /// <summary>Local accounts in the Cube RM external tenant. Invitation only.</summary>
    Local,
    /// <summary>Customer's corporate IdP federated into our tenant.</summary>
    Federated,
    /// <summary>Customer's own Entra tenant trusted directly as an additional issuer (ADR-0004).</summary>
    TrustedIssuer
}
