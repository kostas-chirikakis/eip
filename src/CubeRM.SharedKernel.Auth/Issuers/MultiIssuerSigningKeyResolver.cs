using System.Collections.Concurrent;
using CubeRM.SharedKernel.Auth.Abstractions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CubeRM.SharedKernel.Auth.Issuers;

/// <summary>
/// Resolves signing keys per issuer, each with its own independently refreshed OIDC
/// metadata cache.
/// </summary>
/// <remarks>
/// Per-issuer caching is the point. A customer rotating their signing key must not
/// invalidate, or be blocked by, another issuer's cached metadata.
/// </remarks>
public sealed class MultiIssuerSigningKeyResolver(ITrustedIssuerRegistry registry)
{
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new(StringComparer.Ordinal);

    public IEnumerable<SecurityKey> ResolveKeys(string issuer, string? kid)
    {
        if (string.IsNullOrEmpty(issuer) || !registry.TryGetMetadataAddress(issuer, out var metadataAddress))
        {
            // Unknown issuer: return nothing. Validation then fails for want of a key,
            // which is the correct outcome and leaks nothing about what we do trust.
            return [];
        }

        var manager = _managers.GetOrAdd(issuer, _ =>
            new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = true })
            {
                AutomaticRefreshInterval = TimeSpan.FromHours(12),
                RefreshInterval = TimeSpan.FromMinutes(5)
            });

        // GetConfigurationAsync is cached in-memory after the first call; this blocks only
        // on a cold cache or a refresh. TokenValidationParameters requires a synchronous
        // resolver, which is why this is shaped the way it is.
        var configuration = manager.GetConfigurationAsync(CancellationToken.None)
            .ConfigureAwait(false).GetAwaiter().GetResult();

        return string.IsNullOrEmpty(kid)
            ? configuration.SigningKeys
            : configuration.SigningKeys.Where(k => string.Equals(k.KeyId, kid, StringComparison.Ordinal));
    }
}
