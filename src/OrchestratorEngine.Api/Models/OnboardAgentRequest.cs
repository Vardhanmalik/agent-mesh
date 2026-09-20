using System.Text.Json.Serialization;

namespace OrchestratorEngine.Api.Models;

/// <summary>
/// Payload for <c>POST /api/orchestrator/onboard</c>. Describes a new specialist agent that
/// should be created in Azure AI Foundry and made discoverable by the orchestrator.
/// </summary>
public sealed class OnboardAgentRequest
{
    /// <summary>Display name shown in Foundry and returned to the client. Required.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Short human-readable description used for domain classification and ranking. Required.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// System instructions baked into the agent. When omitted, a domain-neutral default is
    /// synthesized that asks the agent to return JSON offers (matching the ranker's contract).
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// Foundry model deployment name (e.g. <c>gpt-4o-mini</c>, <c>gpt-4.1</c>). Required by the
    /// Foundry create-assistant API; defaults to <c>gpt-4o-mini</c> when the caller doesn't set it.
    /// </summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Free-form tags (e.g. <c>["travel","flights","emirates"]</c>). Persisted in the agent's
    /// metadata as a comma-separated string so it can round-trip through the Foundry list API.
    /// </summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>
    /// Capabilities the orchestrator surfaces to the ranker (e.g. <c>["flight-search","booking"]</c>).
    /// </summary>
    public List<string> Capabilities { get; set; } = [];

    /// <summary>URL of the MCP server this agent should call. Optional.</summary>
    [JsonPropertyName("mcpServerUrl")]
    public string? McpServerUrl { get; set; }

    /// <summary>Friendly label for the MCP server. Defaults to a slug of <see cref="Name"/>.</summary>
    [JsonPropertyName("mcpServerLabel")]
    public string? McpServerLabel { get; set; }

    /// <summary>
    /// MCP server used during the discovery phase (browsing / searching for options). Optional.
    /// When set, the orchestrator prefers this endpoint during <c>/discover</c> fan-out.
    /// Falls back to <see cref="McpServerUrl"/> when not provided.
    /// </summary>
    [JsonPropertyName("mcpServerUrlDiscovery")]
    public string? McpServerUrlDiscovery { get; set; }

    /// <summary>
    /// MCP server used during the execution phase (preparing/committing the selected option).
    /// Optional; used by <c>/execute</c>. Falls back to <see cref="McpServerUrl"/>.
    /// </summary>
    [JsonPropertyName("mcpServerUrlExecution")]
    public string? McpServerUrlExecution { get; set; }

    /// <summary>
    /// MCP server used during the confirmation phase (final commit / payment / booking).
    /// Optional; used by <c>/confirm</c>. Falls back to <see cref="McpServerUrl"/>.
    /// </summary>
    [JsonPropertyName("mcpServerUrlConfirmation")]
    public string? McpServerUrlConfirmation { get; set; }

    /// <summary>
    /// OAuth/OIDC authority the MCP server trusts (e.g. an Entra tenant URL). Persisted in
    /// agent metadata; the MCP tool block also receives it when the Foundry API accepts it.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>Additional key/value metadata written to Foundry's <c>metadata</c> map.</summary>
    public Dictionary<string, string> Metadata { get; set; } = [];
}

/// <summary>
/// Result of a successful onboarding call. Returned so the client can call Discover/Execute
/// with the newly-minted <see cref="AgentId"/> immediately.
/// </summary>
public sealed class OnboardAgentResponse
{
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// User-facing agent identifier derived from <see cref="Name"/> (e.g. "Dominos" →
    /// "dominos-agent"). Kept in step with <see cref="AgentInfo.FriendlyId"/> so onboarding
    /// clients can show the same slug they'd see in the Agent Mesh store.
    /// </summary>
    public string FriendlyId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Surface { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public List<string> Capabilities { get; set; } = [];
    public string? McpServerUrl { get; set; }
    public string? McpServerUrlDiscovery { get; set; }
    public string? McpServerUrlExecution { get; set; }
    public string? McpServerUrlConfirmation { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
