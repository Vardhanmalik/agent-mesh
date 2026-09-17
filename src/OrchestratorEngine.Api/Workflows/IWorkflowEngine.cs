using OrchestratorEngine.Api.Models;

namespace OrchestratorEngine.Api.Workflows;

public interface IWorkflowEngine
{
    Task<OrchestrationResponse> DiscoverAndRecommendAsync(
        string prompt, UserContext userContext, string sessionId, CancellationToken ct = default);

    Task<OrchestrationResponse> ExecuteSelectionAsync(
        string prompt, UserContext userContext, string sessionId, string? optionId, CancellationToken ct = default);

    Task<OrchestrationResponse> ConfirmAndFinalizeAsync(
        string prompt, UserContext userContext, string sessionId, string? optionId, CancellationToken ct = default);
}
