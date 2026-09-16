using CubeRM.Identity.Application.Routing.Queries;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CubeRM.Identity.Api.Endpoints;

/// <summary>
/// <c>POST /auth/discover</c> — email-domain routing (ADR-0003).
/// </summary>
/// <remarks>
/// Unauthenticated and public, so three properties matter more than anything else here:
/// it must not be an enumeration oracle, it must be rate limited, and it must be fast.
/// </remarks>
public static class HomeRealmDiscoveryEndpoint
{
    public static IEndpointRouteBuilder MapHomeRealmDiscovery(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/discover", async (
                DiscoverRequest request,
                ISender sender,
                CancellationToken ct) =>
            {
                var result = await sender.Send(new ResolveHomeRealmQuery(request.Email, request.ClientId), ct);

                return Results.Ok(new DiscoverResponse(
                    Mode: result.Mode,
                    AuthorizeUrl: result.AuthorizeUrl,
                    Message: result.Message));
            })
            .AllowAnonymous()
            .RequireRateLimiting("hrd-per-ip")
            .WithName("DiscoverHomeRealm")
            .WithOpenApi();

        return app;
    }

    public sealed record DiscoverRequest(string Email, string ClientId);

    /// <summary>
    /// <para>
    /// <b>Enumeration resistance.</b> An unknown domain returns <c>mode: "local"</c> —
    /// byte-identical in shape to a known local-account domain. Returning "no such
    /// organisation" would tell an attacker which pharma companies are Cube RM customers,
    /// which is commercially sensitive quite apart from the security concern.
    /// </para>
    /// <para>
    /// The handler equalises response timing across branches too: a database miss is faster
    /// than a hit, and that difference is itself an oracle.
    /// </para>
    /// </summary>
    public sealed record DiscoverResponse(string Mode, string? AuthorizeUrl, string? Message);
}
