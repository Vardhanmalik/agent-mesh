using OrchestratorEngine.Api.Models;

namespace OrchestratorEngine.Api.Services;

public interface IOrchestrationService
{
    Task<OrchestrationResponse> ProcessAsync(OrchestrationRequest request, CancellationToken ct = default);
}
