namespace OrchestratorEngine.Api.Models;

/// <summary>
/// Per-session state written by <c>Discover</c> and read by <c>Execute</c>/<c>Confirm</c>.
/// Preserves the mapping <c>optionId → (agentId, threadId)</c> so a follow-up turn can be
/// routed to the selected agent on the same Foundry thread.
/// </summary>
public sealed class SessionState
{
    public string SessionId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string OriginalPrompt { get; set; } = string.Empty;
    public string PromptDomain { get; set; } = string.Empty;
    public List<SessionOption> Options { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Original prompt that was parked because we asked the user for a delivery/pickup address.
    /// When the user's next turn on this session looks like an address we save it and re-run
    /// discovery using this prompt. Null when no address ask is pending.
    /// </summary>
    public string? PendingPrompt { get; set; }

    /// <summary>Delivery / pickup address in effect for this session (may be echoed for confirmation).</summary>
    public string? DeliveryAddress { get; set; }

    /// <summary>True once the user has confirmed the address for this session.</summary>
    public bool AddressConfirmed { get; set; }
}

public sealed class SessionOption
{
    public string OptionId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string ThreadId { get; set; } = string.Empty;
    public string Surface { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string RawContent { get; set; } = string.Empty;
}
