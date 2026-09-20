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

    /// <summary>
    /// Model / deployment name assigned to agents created via <c>CreateAgentAsync</c> when the
    /// onboarding request does not specify one. This MUST match an existing model deployment in
    /// the Foundry project — otherwise Foundry runs against the resulting agent fail with
    /// <c>last_error.code=invalid_engine_error</c> and message
    /// <c>Failed to resolve model info for: &lt;name&gt;</c>. When empty, the service falls back
    /// to <c>gpt-4o-mini</c> for backward compatibility.
    /// </summary>
    public string DefaultAgentModel { get; set; } = string.Empty;
}
