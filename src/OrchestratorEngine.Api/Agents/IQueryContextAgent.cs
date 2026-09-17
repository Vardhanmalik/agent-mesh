using OrchestratorEngine.Api.Models;

namespace OrchestratorEngine.Api.Agents;

/// <summary>
/// Retrieves query-specific user context from previously ingested embeddings.
/// </summary>
public interface IQueryContextAgent
{
    Task<UserContext> GetQuerySpecificContextAsync(string userId, string query, CancellationToken ct = default);
}