using Azure.Identity;
using Microsoft.SemanticKernel;
using OrchestratorEngine.Api.Agents;
using OrchestratorEngine.Api.Configuration;
using OrchestratorEngine.Api.Services;
using OrchestratorEngine.Api.Workflows;
using OrchestratorEngine.Api.Workflows.Skills;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------
builder.Services.Configure<FoundryOptions>(builder.Configuration.GetSection(FoundryOptions.SectionName));
builder.Services.Configure<VectorStorageOptions>(builder.Configuration.GetSection(VectorStorageOptions.SectionName));
builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection(EmbeddingOptions.SectionName));

// ---------------------------------------------------------------------------
// Semantic Kernel – embedding generation for vector storage
// ---------------------------------------------------------------------------
var embeddingConfig = builder.Configuration.GetSection(EmbeddingOptions.SectionName).Get<EmbeddingOptions>()
    ?? new EmbeddingOptions();

var kernelBuilder = builder.Services.AddKernel();
kernelBuilder.AddAzureOpenAIEmbeddingGenerator(
    deploymentName: embeddingConfig.DeploymentName,
    endpoint: embeddingConfig.Endpoint,
    credential: new DefaultAzureCredential());

// ---------------------------------------------------------------------------
// HTTP client for Azure AI Foundry
// ---------------------------------------------------------------------------
builder.Services.AddHttpClient<IFoundryAgentService, FoundryAgentService>((sp, client) =>
{
    var foundryOptions = builder.Configuration
        .GetSection(FoundryOptions.SectionName).Get<FoundryOptions>() ?? new FoundryOptions();
    client.BaseAddress = new Uri(foundryOptions.Endpoint);
});

// ---------------------------------------------------------------------------
// Application services
// ---------------------------------------------------------------------------
builder.Services.AddSingleton<IVectorStorageService, VectorStorageService>();
builder.Services.AddScoped<IUserContextService, UserContextService>();
builder.Services.AddScoped<IQueryContextAgent, VectorContextAgent>();
builder.Services.AddScoped<ISkillExecutor, SkillExecutor>();
builder.Services.AddScoped<IWorkflowEngine, WorkflowEngine>();
builder.Services.AddScoped<IOrchestrationService, OrchestrationService>();

// ---------------------------------------------------------------------------
// API infrastructure
// ---------------------------------------------------------------------------
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var app = builder.Build();

// ---------------------------------------------------------------------------
// Middleware pipeline
// ---------------------------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Only enforce HTTPS redirection when a cert is actually configured (e.g. local dev / App Service).
// In containers we terminate TLS at the ingress/App Service, so redirecting inside the container
// causes 307s on the /healthz probe and can prevent Kestrel from binding.
if (!app.Environment.IsProduction())
{
    app.UseHttpsRedirection();
}

app.MapControllers();
app.MapHealthChecks("/healthz");

// Ensure vector search index exists on startup — but never let a transient
// Azure-side failure kill the container before it can serve /healthz.
// Otherwise ACI restarts before the logging pipeline flushes any output.
try
{
    using var scope = app.Services.CreateScope();
    var vectorService = scope.ServiceProvider.GetRequiredService<IVectorStorageService>();
    await vectorService.EnsureIndexExistsAsync();
}
catch (Exception ex)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    logger.LogError(ex, "EnsureIndexExistsAsync failed at startup; continuing so the app can serve /healthz and surface the error.");
}

app.Run();
