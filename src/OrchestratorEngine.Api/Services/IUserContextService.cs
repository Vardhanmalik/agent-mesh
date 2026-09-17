using OrchestratorEngine.Api.Models;

namespace OrchestratorEngine.Api.Services;

public interface IUserContextService
{
    Task<UserContext> GetUserContextAsync(string userId, CancellationToken ct = default);
    Task StoreInteractionAsync(string userId, string domain, string summary,
        Dictionary<string, string>? attributes = null, CancellationToken ct = default);
    Task<List<UserEmbeddingDocument>> GetRelevantContextAsync(
        string userId, string query, CancellationToken ct = default);

    /// <summary>
    /// Return the user's most recently saved delivery / pickup address, or null when none.
    /// Backed by <see cref="IVectorStorageService.GetLatestByDomainAsync"/> under domain
    /// <c>delivery-address</c>.
    /// </summary>
    Task<string?> GetSavedAddressAsync(string userId, CancellationToken ct = default);

    /// <summary>
    /// Persist a delivery / pickup address for future turns. Overwrites nothing — the newest
    /// document is used by <see cref="GetSavedAddressAsync"/>.
    /// </summary>
    Task SaveAddressAsync(string userId, string address, CancellationToken ct = default);
}
