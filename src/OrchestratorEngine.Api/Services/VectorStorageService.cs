using Azure;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OrchestratorEngine.Api.Configuration;
using OrchestratorEngine.Api.Models;

namespace OrchestratorEngine.Api.Services;

public sealed class VectorStorageService : IVectorStorageService
{
    private readonly SearchClient _searchClient;
    private readonly SearchIndexClient _indexClient;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly VectorStorageOptions _options;
    private readonly ILogger<VectorStorageService> _logger;

    public VectorStorageService(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IOptions<VectorStorageOptions> options,
        ILogger<VectorStorageService> logger)
    {
        _embeddingGenerator = embeddingGenerator;
        _options = options.Value;
        _logger = logger;

        var credential = new DefaultAzureCredential();
        var endpoint = new Uri(_options.Endpoint);
        _indexClient = new SearchIndexClient(endpoint, credential);
        _searchClient = _indexClient.GetSearchClient(_options.IndexName);
    }

    public async Task EnsureIndexExistsAsync(CancellationToken ct = default)
    {
        var index = BuildIndexDefinition();

        try
        {
            await _indexClient.CreateOrUpdateIndexAsync(index, cancellationToken: ct);
            _logger.LogInformation("Search index '{IndexName}' ensured", _options.IndexName);
        }
        catch (RequestFailedException ex) when (IsIncompatibleSchemaError(ex))
        {
            // The existing index has a schema that predates the current model (e.g. ContentVector
            // used to be a scalar field). Azure AI Search can't convert scalar↔vector in place, so
            // drop and recreate. Safe here because the index holds regenerable embeddings only.
            _logger.LogWarning(ex,
                "Index '{IndexName}' has an incompatible schema; deleting and recreating.",
                _options.IndexName);

            await _indexClient.DeleteIndexAsync(_options.IndexName, ct);
            await _indexClient.CreateIndexAsync(index, ct);
            _logger.LogInformation("Search index '{IndexName}' recreated", _options.IndexName);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex, "Failed to create or update search index '{IndexName}'", _options.IndexName);
            throw;
        }
    }

    private SearchIndex BuildIndexDefinition()
    {
        var vectorSearch = new VectorSearch();
        vectorSearch.Profiles.Add(new VectorSearchProfile("default-profile", "default-algorithm"));
        vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration("default-algorithm"));

        // Explicit field definitions — avoids FieldBuilder ambiguity around ReadOnlyMemory<float>?
        // and guarantees ContentVector is declared as a vector field.
        var fields = new List<SearchField>
        {
            new SimpleField("Id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
            new SimpleField("UserId", SearchFieldDataType.String) { IsFilterable = true },
            new SearchableField("Content") { IsFilterable = false, IsSortable = false },
            new SimpleField("Domain", SearchFieldDataType.String) { IsFilterable = true },
            new SimpleField("CreatedAt", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
            new VectorSearchField(
                name: "ContentVector",
                vectorSearchDimensions: _options.EmbeddingDimensions,
                vectorSearchProfileName: "default-profile")
        };

        return new SearchIndex(_options.IndexName)
        {
            VectorSearch = vectorSearch,
            Fields = fields
        };
    }

    private static bool IsIncompatibleSchemaError(RequestFailedException ex)
    {
        if (ex.Status != 400) return false;
        var msg = ex.Message ?? string.Empty;
        return msg.Contains("is not a vector field", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("cannot be changed", StringComparison.OrdinalIgnoreCase);
    }

    public async Task StoreEmbeddingAsync(string userId, string content, string domain, CancellationToken ct = default)
    {
        var result = await _embeddingGenerator.GenerateAsync(content, cancellationToken: ct);
        var embedding = result.Vector;

        var document = new UserEmbeddingDocument
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Content = content,
            Domain = domain,
            CreatedAt = DateTimeOffset.UtcNow,
            ContentVector = embedding
        };

        await _searchClient.MergeOrUploadDocumentsAsync([document], cancellationToken: ct);
        _logger.LogInformation("Stored embedding for user {UserId} in domain {Domain}", userId, domain);
    }

    public async Task<List<UserEmbeddingDocument>> SearchSimilarAsync(
        string userId, string query, int topK = 5, CancellationToken ct = default)
    {
        var queryResult = await _embeddingGenerator.GenerateAsync(query, cancellationToken: ct);
        var queryEmbedding = queryResult.Vector;

        var searchOptions = new SearchOptions
        {
            Filter = $"UserId eq '{userId}'",
            Size = topK,
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(queryEmbedding)
                    {
                        KNearestNeighborsCount = topK,
                        Fields = { "ContentVector" }
                    }
                }
            }
        };

        var results = new List<UserEmbeddingDocument>();
        await foreach (var result in (await _searchClient.SearchAsync<UserEmbeddingDocument>(
            null, searchOptions, ct)).Value.GetResultsAsync())
        {
            if (result.Document is not null)
                results.Add(result.Document);
        }

        return results;
    }

    public async Task<UserEmbeddingDocument?> GetLatestByDomainAsync(
        string userId, string domain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(domain))
            return null;

        // Filter-only query (no vector similarity) — take the newest matching document.
        // Escape single quotes inline; UserId/domain don't contain them in normal use but
        // OData string literals still require it.
        var safeUser = userId.Replace("'", "''");
        var safeDomain = domain.Replace("'", "''");

        var options = new SearchOptions
        {
            Filter = $"UserId eq '{safeUser}' and Domain eq '{safeDomain}'",
            Size = 1
        };
        options.OrderBy.Add("CreatedAt desc");

        try
        {
            var response = await _searchClient.SearchAsync<UserEmbeddingDocument>("*", options, ct);
            await foreach (var result in response.Value.GetResultsAsync())
            {
                if (result.Document is not null)
                    return result.Document;
            }
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex,
                "GetLatestByDomainAsync failed for user {UserId} domain {Domain}", userId, domain);
        }

        return null;
    }
}
