using System.Collections.Frozen;
using System.Net.Http.Json;
using CubeRM.SharedKernel.Auth.Abstractions;
using CubeRM.SharedKernel.Auth.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CubeRM.SharedKernel.Auth.Issuers;

/// <summary>
/// Periodically refreshed snapshot of trusted issuers and suspended organisations.
/// </summary>
/// <remarks>
/// <para>
/// Read on every token validation, so all lookups are synchronous against an immutable
/// snapshot. Doing I/O inside <c>IssuerValidator</c> would put the identity database on
/// every request's hot path.
/// </para>
/// <para>
/// The snapshot is swapped atomically. Readers always see a complete, consistent view —
/// never a half-updated one.
/// </para>
/// </remarks>
public sealed class TrustedIssuerRegistry : BackgroundService, ITrustedIssuerRegistry
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CubeAuthenticationOptions _options;
    private readonly ILogger<TrustedIssuerRegistry> _logger;

    private volatile Snapshot _snapshot = Snapshot.Empty;

    public TrustedIssuerRegistry(
        IHttpClientFactory httpClientFactory,
        IOptions<CubeAuthenticationOptions> options,
        ILogger<TrustedIssuerRegistry> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsTrusted(string issuer) => _snapshot.Issuers.ContainsKey(issuer);

    public bool TryResolveOrganization(string issuer, out TrustedIssuerOrganization organization)
    {
        if (_snapshot.Issuers.TryGetValue(issuer, out var entry) && entry.Organization is not null)
        {
            organization = entry.Organization;
            return true;
        }
        organization = null!;
        return false;
    }

    public bool TryGetMetadataAddress(string issuer, out string metadataAddress)
    {
        if (_snapshot.Issuers.TryGetValue(issuer, out var entry))
        {
            metadataAddress = entry.MetadataAddress;
            return true;
        }
        metadataAddress = string.Empty;
        return false;
    }

    public bool IsOrganizationSuspended(Guid orgId) => _snapshot.SuspendedOrgIds.Contains(orgId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Load once before serving traffic. A cold registry means every token is untrusted,
        // so this failing at startup must prevent the app reporting healthy.
        await RefreshAsync(stoppingToken);

        using var timer = new PeriodicTimer(_options.IssuerRegistryRefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep serving the last good snapshot. Suspension propagation is delayed,
                // which is why this is alerted on rather than silently retried.
                _logger.LogError(ex,
                    "Trusted issuer registry refresh failed. Serving snapshot from {SnapshotAge} ago.",
                    DateTimeOffset.UtcNow - _snapshot.LoadedAt);
            }
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient(nameof(TrustedIssuerRegistry));
        var response = await client.GetFromJsonAsync<IssuerRegistryResponse>(
            "internal/issuer-registry", cancellationToken)
            ?? throw new InvalidOperationException("Issuer registry endpoint returned no body.");

        var issuers = new Dictionary<string, IssuerEntry>(StringComparer.Ordinal)
        {
            // Our own CIAM tenant. Tokens it issues carry cube_org_id directly, so there is
            // no organisation mapping here — Organization stays null by design.
            [_options.CiamAuthority] = new(
                MetadataAddress: $"{_options.CiamAuthority.TrimEnd('/')}/.well-known/openid-configuration",
                Organization: null)
        };

        if (!string.IsNullOrWhiteSpace(_options.WorkforceAuthority))
        {
            // Cube RM support engineers. No organisation — org scope comes from an audited
            // impersonation grant, never from the token (see §1.4.4).
            issuers[_options.WorkforceAuthority] = new(
                MetadataAddress: $"{_options.WorkforceAuthority.TrimEnd('/')}/.well-known/openid-configuration",
                Organization: null);
        }

        foreach (var trusted in response.TrustedIssuers)
        {
            if (!string.Equals(trusted.Status, "active", StringComparison.OrdinalIgnoreCase))
                continue;

            issuers[trusted.IssuerUrl] = new(
                MetadataAddress: trusted.MetadataAddress,
                Organization: new TrustedIssuerOrganization(
                    trusted.OrgId, trusted.OrgSlug, trusted.EntraTenantId, trusted.AllowedAudiences));
        }

        _snapshot = new Snapshot(
            issuers.ToFrozenDictionary(StringComparer.Ordinal),
            response.SuspendedOrgIds.ToFrozenSet(),
            DateTimeOffset.UtcNow);

        _logger.LogInformation(
            "Issuer registry refreshed: {IssuerCount} issuer(s), {SuspendedCount} suspended organisation(s).",
            issuers.Count, response.SuspendedOrgIds.Count);
    }

    private sealed record IssuerEntry(string MetadataAddress, TrustedIssuerOrganization? Organization);

    private sealed record Snapshot(
        FrozenDictionary<string, IssuerEntry> Issuers,
        FrozenSet<Guid> SuspendedOrgIds,
        DateTimeOffset LoadedAt)
    {
        public static readonly Snapshot Empty = new(
            FrozenDictionary<string, IssuerEntry>.Empty,
            FrozenSet<Guid>.Empty,
            DateTimeOffset.MinValue);
    }

    private sealed record IssuerRegistryResponse(
        IReadOnlyList<TrustedIssuerRecord> TrustedIssuers,
        IReadOnlyList<Guid> SuspendedOrgIds);

    private sealed record TrustedIssuerRecord(
        string IssuerUrl,
        string MetadataAddress,
        Guid OrgId,
        string OrgSlug,
        string EntraTenantId,
        string Status,
        IReadOnlyList<string> AllowedAudiences);
}
