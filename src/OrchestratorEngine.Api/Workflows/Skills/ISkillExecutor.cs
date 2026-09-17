namespace OrchestratorEngine.Api.Workflows.Skills;

public interface ISkillExecutor
{
    Task<T?> ExecuteAsync<T>(string skillName, Dictionary<string, object> parameters, CancellationToken ct = default);
}
