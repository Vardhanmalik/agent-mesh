using Azure.Core;
using Azure.Identity;

namespace OrchestratorEngine.Api.Services;

/// <summary>
/// Attaches an Entra bearer token (audience: https://ai.azure.com) to every outgoing
/// request to Azure AI Foundry. Tokens are cached and refreshed by the underlying
/// <see cref="TokenCredential"/>.
/// </summary>
public sealed class FoundryAuthHandler : DelegatingHandler
{
    // Foundry Agents (services.ai.azure.com) accepts tokens issued for the ai.azure.com resource.
    private static readonly string[] Scopes = ["https://ai.azure.com/.default"];

    private readonly TokenCredential _credential;

    public FoundryAuthHandler(TokenCredential credential)
    {
        _credential = credential;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(Scopes), cancellationToken);

        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

        return await base.SendAsync(request, cancellationToken);
    }
}
