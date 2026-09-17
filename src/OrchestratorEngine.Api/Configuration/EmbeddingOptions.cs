namespace OrchestratorEngine.Api.Configuration;

public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    public string DeploymentName { get; set; } = "text-embedding-ada-002";
    public string Endpoint { get; set; } = string.Empty;
}
