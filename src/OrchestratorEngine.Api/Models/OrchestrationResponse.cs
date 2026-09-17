using System.Text.Json.Serialization;

namespace OrchestratorEngine.Api.Models;

public sealed class OrchestrationResponse
{
    public string SessionId { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OrchestrationStatus Status { get; set; }

    public string Message { get; set; } = string.Empty;
    public List<AgentOption> Options { get; set; } = [];
    public ConfirmationDetails? Confirmation { get; set; }
    public Dictionary<string, object>? Metadata { get; set; }
}

public enum OrchestrationStatus
{
    OptionsAvailable,
    AwaitingSelection,
    Confirmed,
    Failed,

    /// <summary>
    /// The requested action requires a delivery / pickup address the user hasn't provided yet.
    /// The client should prompt the user for an address and re-send it as the next prompt on
    /// the same <c>SessionId</c>. See <c>WorkflowEngine.RequiresDeliveryAddress</c>.
    /// </summary>
    NeedsAddress,

    /// <summary>
    /// A plain conversational reply (address lookup, session recap, greeting, help). No agent
    /// fan-out happened and no options / confirmation are attached — just the <c>Message</c>.
    /// Client should render as a normal chat turn without option cards.
    /// </summary>
    Informational
}

public sealed class AgentOption
{
    // ---- Wire format (matches FoundryResponse.json). Snake-case keys are deliberate. ----

    [JsonPropertyName("option_id")]
    public string OptionId { get; set; } = string.Empty;

    [JsonPropertyName("agent_id")]
    public string AgentId { get; set; } = string.Empty;

    [JsonPropertyName("agent_name")]
    public string AgentName { get; set; } = string.Empty;

    /// <summary>High-level domain classification (e.g. "Travel", "Dining"). Sourced from the workflow's promptDomain.</summary>
    [JsonPropertyName("agent_type")]
    public string AgentType { get; set; } = string.Empty;

    /// <summary>Flat human-readable description of this specific offer (title + description + attributes).</summary>
    [JsonPropertyName("details")]
    public string Details { get; set; } = string.Empty;

    /// <summary>Pre-formatted price string like "700 USD" or "$700". Null when the agent didn't return a price.</summary>
    [JsonPropertyName("price")]
    public string? Price { get; set; }

    // ---- Internal state used by the ranker/session-store; NOT serialized to the wire ----

    [JsonIgnore] public string Summary { get; set; } = string.Empty;
    [JsonIgnore] public string? Title { get; set; }
    [JsonIgnore] public string? Description { get; set; }
    [JsonIgnore] public decimal? PriceValue { get; set; }
    [JsonIgnore] public string? Currency { get; set; }
    [JsonIgnore] public Dictionary<string, string> Attributes { get; set; } = [];

    /// <summary>Raw agent invocation payload — kept for session-store threading and Execute continuation.</summary>
    [JsonIgnore] public Dictionary<string, object> Raw { get; set; } = [];
}

public sealed class ConfirmationDetails
{
    public string AgentId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ConfirmationId { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public Dictionary<string, object> Data { get; set; } = [];
}
