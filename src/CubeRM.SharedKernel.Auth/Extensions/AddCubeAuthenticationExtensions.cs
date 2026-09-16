using CubeRM.SharedKernel.Auth.Abstractions;
using CubeRM.SharedKernel.Auth.Claims;
using CubeRM.SharedKernel.Auth.Configuration;
using CubeRM.SharedKernel.Auth.Issuers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CubeRM.SharedKernel.Auth.Extensions;

/// <summary>
/// The entire authentication surface a Cube RM API needs.
/// </summary>
/// <remarks>
/// One package, one call, one place to fix a token-validation bug across every service.
/// The alternative — each API configuring JwtBearer itself — guarantees drift, and drift in
/// token validation is a security defect rather than an inconsistency.
/// </remarks>
public static class AddCubeAuthenticationExtensions
{
    public static IServiceCollection AddCubeAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<CubeAuthenticationOptions>()
            .Bind(configuration.GetSection(CubeAuthenticationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddScoped<CubeTenantContext>();
        services.TryAddScoped<ICubeTenantContext>(sp => sp.GetRequiredService<CubeTenantContext>());
        services.TryAddScoped<ICubeTenantContextSetter>(sp => sp.GetRequiredService<CubeTenantContext>());

        services.AddSingleton<TrustedIssuerRegistry>();
        services.AddSingleton<ITrustedIssuerRegistry>(sp => sp.GetRequiredService<TrustedIssuerRegistry>());
        services.AddHostedService(sp => sp.GetRequiredService<TrustedIssuerRegistry>());
        services.AddSingleton<MultiIssuerSigningKeyResolver>();

        services.AddHttpClient(nameof(TrustedIssuerRegistry), (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<CubeAuthenticationOptions>>().Value;
            client.BaseAddress = new Uri(options.IdentityServiceBaseAddress);
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        // Configured through IConfigureOptions rather than inside AddJwtBearer's callback,
        // so dependencies come from the real container. Calling BuildServiceProvider() in
        // the callback would construct a second container and give singletons twice.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<ITrustedIssuerRegistry, MultiIssuerSigningKeyResolver, IOptions<CubeAuthenticationOptions>>(
                ConfigureJwtBearer);

        services.AddAuthorization();
        return services;
    }

    private static void ConfigureJwtBearer(
        JwtBearerOptions jwt,
        ITrustedIssuerRegistry registry,
        MultiIssuerSigningKeyResolver keyResolver,
        IOptions<CubeAuthenticationOptions> cubeOptions)
    {
        var options = cubeOptions.Value;

        // Single scheme with a dynamic issuer resolver, not N schemes. N schemes would
        // force [Authorize] to name one, which leaks issuer topology into endpoint code
        // that has no business knowing about it.
        jwt.Authority = options.CiamAuthority;
        jwt.MapInboundClaims = false;   // keep short claim names: oid, tid, cube_org_id

        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            IssuerValidator = (issuer, _, _) => registry.IsTrusted(issuer)
                ? issuer
                // Message is deliberately non-specific about what we do trust.
                : throw new SecurityTokenInvalidIssuerException("Issuer is not trusted."),

            // Per-issuer JWKS, independently cached and refreshed. A customer rotating
            // their signing key must not disturb another issuer's cached metadata.
            IssuerSigningKeyResolver = (_, securityToken, kid, _) =>
                keyResolver.ResolveKeys(securityToken.Issuer, kid),

            ValidateAudience = true,
            ValidAudiences = options.ValidAudiences,

            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,

            // 30 seconds, not the framework default of 5 minutes. The default silently
            // extends every token past expiry and undercuts the offboarding SLA in
            // docs/identity/06-offboarding-incident.md.
            ClockSkew = options.ClockSkew,

            NameClaimType = "email",
            RoleClaimType = CubeClaimTypes.Roles   // present so stray roles are visible, never used for authz
        };

        jwt.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = ctx =>
            {
                ctx.NoResult();
                return Task.CompletedTask;
            },
            OnChallenge = ctx =>
            {
                ctx.Response.Headers.Remove("WWW-Authenticate");
                return Task.CompletedTask;
            }
        };
    }

    /// <summary>Populates <see cref="ICubeTenantContext"/>. Must follow <c>UseAuthentication</c>.</summary>
    public static IApplicationBuilder UseCubeTenantContext(this IApplicationBuilder app) =>
        app.UseMiddleware<CubeTenantContextMiddleware>();
}
