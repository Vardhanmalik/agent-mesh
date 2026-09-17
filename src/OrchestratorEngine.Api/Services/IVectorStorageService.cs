using OrchestratorEngine.Api.Models;

namespace OrchestratorEngine.Api.Services;

public interface IVectorStorageService
{
    Task StoreEmbeddingAsync(string userId, string content, string domain, CancellationToken ct = default);
    Task<List<UserEmbeddingDocument>> SearchSimilarAsync(string userId, string query, int topK = 5, CancellationToken ct = default);
    Task EnsureIndexExistsAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetch the most recent embedding for a given (userId, domain) using a filter-only query
    /// (no vector similarity). Used for slot-typed lookups like the user's saved delivery address.
    /// Returns null when no matching document exists.
    /// </summary>
    Task<UserEmbeddingDocument?> GetLatestByDomainAsync(string userId, string domain, CancellationToken ct = default);
}
