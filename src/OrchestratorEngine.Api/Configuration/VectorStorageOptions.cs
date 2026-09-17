namespace OrchestratorEngine.Api.Configuration;

public sealed class VectorStorageOptions
{
    public const string SectionName = "VectorStorage";

    public string Endpoint { get; set; } = string.Empty;
    public string IndexName { get; set; } = "user-context";
    public int EmbeddingDimensions { get; set; } = 1536;
}
