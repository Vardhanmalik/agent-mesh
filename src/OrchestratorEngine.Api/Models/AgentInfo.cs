namespace OrchestratorEngine.Api.Models;

public sealed class AgentInfo
{
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable slug derived from the agent's <see cref="Name"/> (e.g. "Dominos" →
    /// "dominos-agent"). Used as the primary user-facing identifier in the Agent Mesh store
    /// UI while <see cref="AgentId"/> retains the opaque Foundry-assigned handle needed for
    /// invocation. Populated on both list and create paths.
    /// </summary>
    public string FriendlyId { get; set; } = string.Empty;

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

    /// <summary>
    /// Build the friendly slug for a given display name: lowercase, only [a-z0-9-],
    /// collapsed dashes, always suffixed with "-agent". Empty when <paramref name="name"/> is blank.
    /// </summary>
    public static string BuildFriendlyId(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var sb = new System.Text.StringBuilder(name.Length + 6);
        var prevDash = false;
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                prevDash = false;
            }
            else if (!prevDash && sb.Length > 0)
            {
                sb.Append('-');
                prevDash = true;
            }
        }
        if (sb.Length > 0 && sb[^1] == '-') sb.Length--;
        // Strip a redundant trailing "-agent" (many onboarded names already end in "Agent")
        // so we don't produce "dominos-agent-agent".
        var slug = sb.ToString();
        if (slug.EndsWith("-agent", StringComparison.Ordinal)) return slug;
        return string.IsNullOrEmpty(slug) ? string.Empty : slug + "-agent";
    }
}
