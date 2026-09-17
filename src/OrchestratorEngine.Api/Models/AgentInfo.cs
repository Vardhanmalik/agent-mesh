namespace OrchestratorEngine.Api.Models;

public sealed class AgentInfo
{
    public string AgentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public List<string> Capabilities { get; set; } = [];
    public string McpServerEndpoint { get; set; } = string.Empty;
    public Dictionary<string, object> Configuration { get; set; } = [];

    /// <summary>
    /// Which Foundry data-plane surface this agent came from. Used by <c>FoundryAgentService.InvokeAgentAsync</c>
    /// to pick the correct runtime pattern (OpenAI-Assistants vs. Foundry Agent Service v1 hosted-agents).
    /// Values: <c>"assistants"</c> or <c>"agents-v1"</c>.
    /// </summary>
    public string Surface { get; set; } = "assistants";
}
