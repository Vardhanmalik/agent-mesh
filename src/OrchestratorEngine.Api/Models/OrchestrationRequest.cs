using System.Text.Json.Serialization;

namespace OrchestratorEngine.Api.Models;

public sealed class OrchestrationRequest
{
    public string UserId { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string? SessionId { get; set; }

    /// <summary>
    /// The option chosen by the user from a prior <c>Discover</c> response. Required on
    /// <c>Execute</c> (and typically <c>Confirm</c>) — it's how the orchestrator knows
    /// which agent + thread to continue on.
    /// </summary>
    public string? OptionId { get; set; }

    /// <summary>
    /// When set on a <c>Discover</c> call, the workflow scopes fan-out to just this agent
    /// ("Chat with agent" solo mode in the sample UI). Thread continuity is preserved when
    /// the agent already has a thread in the session.
    /// </summary>
    public string? PreferredAgentId { get; set; }

    public UserContext? Context { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OrchestrationIntent Intent { get; set; } = OrchestrationIntent.Discover;
}

public enum OrchestrationIntent
{
    Discover,
    Execute,
    Confirm
}
