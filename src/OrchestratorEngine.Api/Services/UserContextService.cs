using OrchestratorEngine.Api.Models;

namespace OrchestratorEngine.Api.Services;

public sealed class UserContextService : IUserContextService
{
    private readonly IVectorStorageService _vectorStorage;
    private readonly ILogger<UserContextService> _logger;

    public UserContextService(
        IVectorStorageService vectorStorage,
        ILogger<UserContextService> logger)
    {
        _vectorStorage = vectorStorage;
        _logger = logger;
    }

    public async Task<UserContext> GetUserContextAsync(string userId, CancellationToken ct = default)
    {
        var relevantDocs = await _vectorStorage.SearchSimilarAsync(userId, "user preferences and history", 10, ct);

        var context = new UserContext
        {
            UserId = userId,
            Preferences = [],
            PastInteractions = []
        };

        foreach (var doc in relevantDocs)
        {
            if (doc.Domain == "preference")
            {
                context.Preferences[doc.Id] = doc.Content;
            }
            else
            {
                context.PastInteractions.Add(new PastInteraction
                {
                    Domain = doc.Domain,
                    Summary = doc.Content,
                    Timestamp = doc.CreatedAt
                });
            }
        }

        _logger.LogInformation("Retrieved context for user {UserId}: {PrefCount} preferences, {InteractionCount} interactions",
            userId, context.Preferences.Count, context.PastInteractions.Count);

        return context;
    }

    public async Task StoreInteractionAsync(string userId, string domain, string summary,
        Dictionary<string, string>? attributes = null, CancellationToken ct = default)
    {
        var content = attributes != null
            ? $"{summary} | {string.Join(", ", attributes.Select(a => $"{a.Key}={a.Value}"))}"
            : summary;

        await _vectorStorage.StoreEmbeddingAsync(userId, content, domain, ct);
        _logger.LogInformation("Stored interaction for user {UserId} in domain {Domain}", userId, domain);
    }

    public async Task<List<UserEmbeddingDocument>> GetRelevantContextAsync(
        string userId, string query, CancellationToken ct = default)
    {
        return await _vectorStorage.SearchSimilarAsync(userId, query, 5, ct);
    }

    // ---- Delivery address slot (domain = "delivery-address") ----

    private const string DeliveryAddressDomain = "delivery-address";

    public async Task<string?> GetSavedAddressAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;
        var doc = await _vectorStorage.GetLatestByDomainAsync(userId, DeliveryAddressDomain, ct);
        return string.IsNullOrWhiteSpace(doc?.Content) ? null : doc!.Content;
    }

    public async Task SaveAddressAsync(string userId, string address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(address)) return;
        await _vectorStorage.StoreEmbeddingAsync(userId, address.Trim(), DeliveryAddressDomain, ct);
        _logger.LogInformation("Saved delivery address for user {UserId}", userId);
    }
}
