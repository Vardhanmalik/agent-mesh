using Microsoft.AspNetCore.Mvc;
using OrchestratorEngine.Api.Models;
using OrchestratorEngine.Api.Services;

namespace OrchestratorEngine.Api.Controllers;

[ApiController]
[Route("api/orchestrator/")]
public sealed class OrchestrationController : ControllerBase
{
    private readonly IOrchestrationService _orchestrationService;
    private readonly IFoundryAgentService _foundryAgentService;
    private readonly ILogger<OrchestrationController> _logger;

    public OrchestrationController(
        IOrchestrationService orchestrationService,
        IFoundryAgentService foundryAgentService,
        ILogger<OrchestrationController> logger)
    {
        _orchestrationService = orchestrationService;
        _foundryAgentService = foundryAgentService;
        _logger = logger;
    }

    /// <summary>
    /// Primary orchestration endpoint for Copilot integration.
    /// Accepts a user prompt and context, routes to the appropriate Foundry agent,
    /// and returns structured options or confirmations.
    /// </summary>
    [HttpPost("process")]
    [ProducesResponseType(typeof(OrchestrationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Process(
        [FromBody] OrchestrationRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            return BadRequest(new { error = "UserId is required." });

        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest(new { error = "Prompt is required." });

        _logger.LogInformation("Orchestration request received from user {UserId}, intent {Intent}",
            request.UserId, request.Intent);

        var response = await _orchestrationService.ProcessAsync(request, ct);
        return Ok(response);
    }

    /// <summary>
    /// Discover available agents for a given prompt domain.
    /// </summary>
    [HttpPost("discover")]
    [ProducesResponseType(typeof(OrchestrationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Discover(
        [FromBody] OrchestrationRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            return BadRequest(new { error = "UserId is required." });

        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest(new { error = "Prompt is required." });

        request.Intent = OrchestrationIntent.Discover;
        var response = await _orchestrationService.ProcessAsync(request, ct);
        return Ok(response);
    }

    /// <summary>
    /// Execute a user's selection from previously offered options.
    /// Called when the user clicks "Choose" on a Copilot-presented option. The follow-up
    /// prompt (e.g. "window seat, depart after 6pm") is forwarded to the chosen agent on
    /// the same Foundry thread that produced the option.
    /// </summary>
    [HttpPost("execute")]
    [ProducesResponseType(typeof(OrchestrationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Execute(
        [FromBody] OrchestrationRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            return BadRequest(new { error = "UserId is required." });

        if (string.IsNullOrWhiteSpace(request.SessionId))
            return BadRequest(new { error = "SessionId is required for execution." });

        if (string.IsNullOrWhiteSpace(request.OptionId))
            return BadRequest(new { error = "OptionId is required for execution. Pass the optionId from a prior /discover response." });

        // Prompt is optional on execute — the user's original request already lives on the thread.
        // If the caller sends nothing we ask the agent to proceed with the selected option.
        if (string.IsNullOrWhiteSpace(request.Prompt))
            request.Prompt = "Proceed with this option. Ask for any details you still need to complete the request.";

        request.Intent = OrchestrationIntent.Execute;
        var response = await _orchestrationService.ProcessAsync(request, ct);
        return Ok(response);
    }

    /// <summary>
    /// Confirm a booking/action after the user has reviewed details.
    /// </summary>
    [HttpPost("confirm")]
    [ProducesResponseType(typeof(OrchestrationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Confirm(
        [FromBody] OrchestrationRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserId))
            return BadRequest(new { error = "UserId is required." });

        if (string.IsNullOrWhiteSpace(request.SessionId))
            return BadRequest(new { error = "SessionId is required for confirmation." });

        if (string.IsNullOrWhiteSpace(request.OptionId))
            return BadRequest(new { error = "OptionId is required for confirmation." });

        request.Intent = OrchestrationIntent.Confirm;
        var response = await _orchestrationService.ProcessAsync(request, ct);
        return Ok(response);
    }

    /// <summary>
    /// Health check endpoint.
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Health() => Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow });

    /// <summary>
    /// List every agent the orchestrator can see across both Foundry data-plane surfaces
    /// (OpenAI-Assistants and Foundry Agent Service v1). Each item carries a
    /// <see cref="AgentInfo.Configuration"/> snapshot — model, tags, capabilities, MCP endpoints,
    /// tools, metadata — so callers (e.g. the Agent Mesh Platform onboarding UI) can render a
    /// full config view without making per-agent round-trips.
    /// </summary>
    [HttpGet("agents")]
    [ProducesResponseType(typeof(IEnumerable<AgentInfo>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListAgents(CancellationToken ct)
    {
        var agents = await _foundryAgentService.ListAvailableAgentsAsync(ct);
        _logger.LogInformation("ListAgents returning {Count} agents", agents.Count);
        return Ok(agents);
    }

    /// <summary>
    /// Onboard a new specialist agent into the orchestrator. Provisions the agent in Azure AI
    /// Foundry (OpenAI-Assistants surface) using the supplied name, description, model, MCP
    /// server, tags, and authority — then makes it immediately routable by <c>/discover</c>
    /// and <c>/execute</c> without waiting for the Foundry list cache to refresh.
    /// </summary>
    [HttpPost("onboard")]
    [ProducesResponseType(typeof(OnboardAgentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Onboard(
        [FromBody] OnboardAgentRequest request,
        CancellationToken ct)
    {
        if (request is null)
            return BadRequest(new { error = "Request body is required." });
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required." });
        if (string.IsNullOrWhiteSpace(request.Description))
            return BadRequest(new { error = "Description is required." });
        if (string.IsNullOrWhiteSpace(request.Model))
            return BadRequest(new { error = "Model is required (e.g. \"gpt-4o-mini\")." });

        _logger.LogInformation(
            "Onboard request received for agent '{Name}' (model={Model}, mcp={HasMcp})",
            request.Name, request.Model, !string.IsNullOrWhiteSpace(request.McpServerUrl));

        AgentInfo created;
        try
        {
            created = await _foundryAgentService.CreateAgentAsync(request, ct);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Onboarding failed for agent '{Name}'", request.Name);
            return StatusCode(StatusCodes.Status502BadGateway,
                new { error = "Foundry agent creation failed.", detail = ex.Message });
        }

        var response = new OnboardAgentResponse
        {
            AgentId = created.AgentId,
            FriendlyId = string.IsNullOrEmpty(created.FriendlyId)
                ? AgentInfo.BuildFriendlyId(created.Name)
                : created.FriendlyId,
            Name = created.Name,
            Description = created.Description,
            Model = request.Model,
            Surface = created.Surface,
            Tags = request.Tags,
            Capabilities = request.Capabilities,
            McpServerUrl = string.IsNullOrEmpty(created.McpServerEndpoint) ? null : created.McpServerEndpoint,
            McpServerUrlDiscovery = string.IsNullOrWhiteSpace(request.McpServerUrlDiscovery) ? null : request.McpServerUrlDiscovery,
            McpServerUrlExecution = string.IsNullOrWhiteSpace(request.McpServerUrlExecution) ? null : request.McpServerUrlExecution,
            McpServerUrlConfirmation = string.IsNullOrWhiteSpace(request.McpServerUrlConfirmation) ? null : request.McpServerUrlConfirmation,
            CreatedAt = DateTimeOffset.UtcNow
        };

        return CreatedAtAction(nameof(Onboard), new { id = created.AgentId }, response);
    }
}
