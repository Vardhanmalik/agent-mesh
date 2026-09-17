namespace OrchestratorEngine.Api.Configuration;

public sealed class FoundryOptions
{
    public const string SectionName = "AzureFoundry";

    public string Endpoint { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;

    /// <summary>
    /// Azure AI Foundry Agents data-plane API version. The OpenAI-compatible Assistants surface
    /// (which is what the Foundry portal creates when you build an agent in the Agents playground)
    /// uses preview versions like "2025-05-15-preview". The newer hosted-agents surface uses "v1".
    /// This service currently calls the OpenAI-compatible /assistants endpoint.
    /// </summary>
    public string ApiVersion { get; set; } = "2025-05-15-preview";

    /// <summary>
    /// Foundry agent ID used to make the final agent-selection decision. When set, the heuristic
    /// candidate scores are passed to this agent for a final LLM-assisted pick. When empty, agent
    /// selection falls back to the heuristic ranking alone.
    /// </summary>
    public string SelectorAgentId { get; set; } = string.Empty;
}
