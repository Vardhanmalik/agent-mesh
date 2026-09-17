using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace OrchestratorEngine.Api.Models;

public sealed class UserEmbeddingDocument
{
    [SimpleField(IsKey = true, IsFilterable = true)]
    public string Id { get; set; } = string.Empty;

    [SimpleField(IsFilterable = true)]
    public string UserId { get; set; } = string.Empty;

    [SearchableField]
    public string Content { get; set; } = string.Empty;

    [SimpleField(IsFilterable = true)]
    public string Domain { get; set; } = string.Empty;

    [SimpleField(IsFilterable = true, IsSortable = true)]
    public DateTimeOffset CreatedAt { get; set; }

    [VectorSearchField(
        VectorSearchDimensions = 1536,
        VectorSearchProfileName = "default-profile")]
    public ReadOnlyMemory<float>? ContentVector { get; set; }
}
