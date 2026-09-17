namespace OrchestratorEngine.Api.Models;

public sealed class UserContext
{
    public string UserId { get; set; } = string.Empty;
    public Dictionary<string, string> Preferences { get; set; } = [];
    public List<PastInteraction> PastInteractions { get; set; } = [];
}

public sealed class PastInteraction
{
    public string Domain { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = [];
}
