using OrchestratorEngine.Api.Models;
using OrchestratorEngine.Api.Services;

namespace OrchestratorEngine.Api.Agents;

/// <summary>
/// Query-context agent that uses vector similarity search over previously stored user data.
/// </summary>
public sealed class VectorContextAgent : IQueryContextAgent
{
    private const int MaxItemsPerQuery = 10;
    private const int MaxContentLengthChars = 2000;

    private readonly IVectorStorageService _vectorStorage;
    private readonly ILogger<VectorContextAgent> _logger;

    public VectorContextAgent(
        IVectorStorageService vectorStorage,
        ILogger<VectorContextAgent> logger)
    {
        _vectorStorage = vectorStorage;
        _logger = logger;
    }

    public async Task<UserContext> GetQuerySpecificContextAsync(
        string userId,
        string query,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var similarDocs = await _vectorStorage.SearchSimilarAsync(userId, query, MaxItemsPerQuery, ct);
        var context = new UserContext { UserId = userId };

        foreach (var doc in similarDocs.Where(d => !string.IsNullOrWhiteSpace(d.Content)))
        {
            var content = doc.Content.Length <= MaxContentLengthChars
                ? doc.Content
                : doc.Content[..MaxContentLengthChars];

            if (doc.Domain.Equals("preference", StringComparison.OrdinalIgnoreCase))
            {
                context.Preferences[doc.Id] = content;
                continue;
            }

            context.PastInteractions.Add(new PastInteraction
            {
                Domain = doc.Domain,
                Summary = content,
                Timestamp = doc.CreatedAt
            });
        }

        _logger.LogInformation(
            "Retrieved {Count} vector-context item(s) for user {UserId}",
            context.Preferences.Count + context.PastInteractions.Count,
            userId);

        return context;
    }
}