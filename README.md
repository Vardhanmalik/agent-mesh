# OrchestratorEngine

A C# backend orchestration service that integrates with **Microsoft 365 Copilot** and **Azure AI Foundry** to route user requests to the correct AI agent via MCP servers.

## Architecture

```
User Prompt (M365 Copilot) ──► Orchestration API ──► Workflow Engine
                                                     │
                                    ┌────────────────┼────────────────┐
                                    ▼                ▼                ▼
                             Fetch Agents     Select Best Agent   Execute Agent
                            (Foundry API)    (Scoring + RAG)    (MCP Server)
                                                     │
                                                     ▼
                                              Vector Storage
                                          (Azure AI Search - RAG)
```

## API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/orchestration/process` | Primary orchestration endpoint |
| POST | `/api/orchestration/discover` | Discover agents for a prompt |
| POST | `/api/orchestration/execute` | Execute a chosen option |
| POST | `/api/orchestration/confirm` | Confirm a booking/action |
| GET  | `/api/orchestration/health` | Health check |
| GET  | `/healthz` | Liveness probe |

## Flow Example — Flight Booking

1. **User prompt**: *"Book me a flight from Delhi to London on 18th September 2026"*
2. **Discover**: Orchestrator fetches agents from Azure AI Foundry, selects travel/flight agents (e.g., Air India, Emirates agents), checks user's stored preferences (timing, food, seat), returns 1-2 best options.
3. **Choose**: M365 Copilot shows options with a **Choose** button. User clicks one.
4. **Execute**: Orchestrator invokes the selected agent's MCP server with the booking details.
5. **Confirm**: Agent returns confirmation details. Interaction is stored as an embedding for future RAG.

## Configuration

Set the following in `appsettings.json` or environment variables:

| Section | Key | Description |
|---------|-----|-------------|
| `AzureFoundry` | `Endpoint` | Azure AI Foundry project endpoint |
| `VectorStorage` | `Endpoint` | Azure AI Search endpoint |
| `Embedding` | `Endpoint` | Azure OpenAI endpoint for embeddings |
| `Embedding` | `DeploymentName` | Embedding model deployment name |

Authentication uses `DefaultAzureCredential` (Managed Identity in Azure, CLI/VS locally).

## Docker

```bash
# Build
docker build -t orchestrator-engine .

# Run
docker run -p 8080:8080 \
  -e AzureFoundry__Endpoint="https://..." \
  -e VectorStorage__Endpoint="https://..." \
  -e Embedding__Endpoint="https://..." \
  orchestrator-engine
```

Or with docker-compose:

```bash
docker-compose up --build
```

## Local Development

```bash
cd src/OrchestratorEngine.Api
dotnet run
```

## Project Structure

```
OrchestratorEngine/
├── Dockerfile
├── docker-compose.yml
├── OrchestratorEngine.sln
└── src/OrchestratorEngine.Api/
    ├── Configuration/        # Options classes (Foundry, VectorStorage, Embedding)
    ├── Controllers/          # API controllers
    ├── Models/               # Request/Response DTOs, domain models
    ├── Services/             # Business services (Orchestration, VectorStorage, Foundry, UserContext)
    └── Workflows/
        ├── WorkflowEngine.cs # Discover → Execute → Confirm pipeline
        └── Skills/
            ├── skills.json   # Skill definitions (agent discovery, selection, execution, confirmation)
            ├── SkillNames.cs # Skill name constants
            └── SkillExecutor.cs  # Skill routing and execution
```
