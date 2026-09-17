using OrchestratorEngine.Api.Models;

namespace OrchestratorEngine.Api.Services;

public interface IFoundryAgentService
{
    Task<List<AgentInfo>> ListAvailableAgentsAsync(CancellationToken ct = default);
    Task<AgentInfo?> GetAgentAsync(string agentId, CancellationToken ct = default);

    /// <summary>
    /// Invoke a Foundry agent. When <paramref name="threadId"/> is supplied the message is posted
    /// to that existing thread and a new run is created on it (used for follow-up turns like
    /// "window seat, 6pm departure"). Otherwise a fresh thread+run is created in one call.
    /// </summary>
    Task<Dictionary<string, object>> InvokeAgentAsync(
        string agentId,
        string prompt,
        Dictionary<string, object>? parameters = null,
        string? threadId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Create a new agent in Azure AI Foundry (OpenAI-Assistants surface) from an onboarding
    /// request and warm the local surface/endpoint caches so it can be invoked immediately.
    /// </summary>
    Task<AgentInfo> CreateAgentAsync(OnboardAgentRequest request, CancellationToken ct = default);
}
