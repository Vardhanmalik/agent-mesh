using OrchestratorEngine.Api.Agents;
using OrchestratorEngine.Api.Models;
using OrchestratorEngine.Api.Workflows;

namespace OrchestratorEngine.Api.Services;

public sealed class OrchestrationService : IOrchestrationService
{
    private readonly IWorkflowEngine _workflowEngine;
    private readonly IUserContextService _userContextService;
    private readonly IQueryContextAgent _queryContextAgent;
    private readonly IVectorStorageService _vectorStorage;
    private readonly ILogger<OrchestrationService> _logger;

    public OrchestrationService(
        IWorkflowEngine workflowEngine,
        IUserContextService userContextService,
        IQueryContextAgent queryContextAgent,
        IVectorStorageService vectorStorage,
        ILogger<OrchestrationService> logger)
    {
        _workflowEngine = workflowEngine;
        _userContextService = userContextService;
        _queryContextAgent = queryContextAgent;
        _vectorStorage = vectorStorage;
        _logger = logger;
    }

    public async Task<OrchestrationResponse> ProcessAsync(OrchestrationRequest request, CancellationToken ct = default)
    {
        var sessionId = request.SessionId ?? Guid.NewGuid().ToString("N");
        _logger.LogInformation("Processing orchestration request for user {UserId}, session {SessionId}, intent {Intent}",
            request.UserId, sessionId, request.Intent);

        // Step 1: Retrieve query-specific user context from previously ingested embeddings.
        // This keeps retrieval scoped to what is relevant for the current prompt.
        var userContext = request.Context
            ?? await _queryContextAgent.GetQuerySpecificContextAsync(request.UserId, request.Prompt, ct);

        // Step 2: Store the incoming prompt as an embedding for future RAG
        await _vectorStorage.StoreEmbeddingAsync(
            request.UserId,
            request.Prompt,
            "interaction",
            ct);

        // Step 3: Execute the appropriate workflow based on intent
        var response = request.Intent switch
        {
            OrchestrationIntent.Discover => await _workflowEngine.DiscoverAndRecommendAsync(
                request.Prompt, userContext, sessionId, ct),

            OrchestrationIntent.Execute => await _workflowEngine.ExecuteSelectionAsync(
                request.Prompt, userContext, sessionId, request.OptionId, ct),

            OrchestrationIntent.Confirm => await _workflowEngine.ConfirmAndFinalizeAsync(
                request.Prompt, userContext, sessionId, request.OptionId, ct),

            _ => new OrchestrationResponse
            {
                SessionId = sessionId,
                Status = OrchestrationStatus.Failed,
                Message = $"Unknown intent: {request.Intent}"
            }
        };

        // Step 4: Store the result as context for future interactions
        await _userContextService.StoreInteractionAsync(
            request.UserId,
            "orchestration-result",
            $"Intent: {request.Intent} | Prompt: {request.Prompt} | Status: {response.Status}",
            ct: ct);

        return response;
    }
}
