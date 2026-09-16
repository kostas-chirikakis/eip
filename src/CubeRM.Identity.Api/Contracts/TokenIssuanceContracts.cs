using System.Text.Json.Serialization;

namespace CubeRM.Identity.Api.Contracts;

/// <summary>
/// Wire contract for the Entra <c>OnTokenIssuanceStart</c> custom authentication extension.
/// </summary>
/// <remarks>
/// Shapes follow the documented Microsoft Graph callout schema. The exact payload is one of
/// the things risk register <b>R2</b> validates against a live tenant before this is
/// load-bearing — verify field names and the <c>@odata.type</c> discriminators against a
/// captured request rather than trusting these constants.
/// </remarks>
public sealed record TokenIssuanceStartRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("data")] TokenIssuanceData Data);

public sealed record TokenIssuanceData(
    [property: JsonPropertyName("@odata.type")] string ODataType,
    [property: JsonPropertyName("tenantId")] string TenantId,
    [property: JsonPropertyName("authenticationEventListenerId")] string? ListenerId,
    [property: JsonPropertyName("customAuthenticationExtensionId")] string? ExtensionId,
    [property: JsonPropertyName("authenticationContext")] AuthenticationContext AuthenticationContext);

public sealed record AuthenticationContext(
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("user")] TokenIssuanceUser? User);

/// <param name="Id">The directory object id. This — not <c>sub</c> — is the durable user key.</param>
public sealed record TokenIssuanceUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("mail")] string? Mail,
    [property: JsonPropertyName("userPrincipalName")] string? UserPrincipalName,
    [property: JsonPropertyName("displayName")] string? DisplayName);

/// <summary>Response shape. An empty <c>actions</c> array means "issue the token with no extra claims".</summary>
public sealed record TokenIssuanceStartResponse(
    [property: JsonPropertyName("data")] TokenIssuanceResponseData Data)
{
    public const string ResponseODataType = "microsoft.graph.onTokenIssuanceStartResponseData";
    public const string ProvideClaimsODataType = "microsoft.graph.tokenIssuanceStart.provideClaimsForToken";

    /// <summary>
    /// The fail-closed response. The token is still issued, but without
    /// <c>cube_org_id</c> — and both APIM and every API reject a token lacking it. An
    /// unexpected authentication path therefore yields a useless token rather than a
    /// mis-scoped one.
    /// </summary>
    public static TokenIssuanceStartResponse NoClaims() =>
        new(new TokenIssuanceResponseData(ResponseODataType, []));

    public static TokenIssuanceStartResponse WithClaims(IReadOnlyDictionary<string, string> claims) =>
        new(new TokenIssuanceResponseData(
            ResponseODataType,
            [new ProvideClaimsAction(ProvideClaimsODataType, claims)]));
}

public sealed record TokenIssuanceResponseData(
    [property: JsonPropertyName("@odata.type")] string ODataType,
    [property: JsonPropertyName("actions")] IReadOnlyList<ProvideClaimsAction> Actions);

public sealed record ProvideClaimsAction(
    [property: JsonPropertyName("@odata.type")] string ODataType,
    [property: JsonPropertyName("claims")] IReadOnlyDictionary<string, string> Claims);
