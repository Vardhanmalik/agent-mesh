# Agent Mesh Platform — sample UI

A zero-dependency sample UI that acts as the **onboarding & catalog surface** for the
OrchestratorEngine. From here you can:

- **Onboard a new agent** — a modal form collects the fields required by the
  `POST /api/orchestrator/onboard` API (name, description, model, MCP endpoints,
  tags, capabilities, authority, …). Submitting creates the agent in Azure AI Foundry
  and makes it immediately routable to Copilot.
- **Browse the mesh catalog** — every agent visible to the orchestrator, fetched
  from the new `GET /api/orchestrator/agents` endpoint, grouped into categories
  (laptops, food, travel, finance, support, media, shopping, …). Categorization is
  derived from the agent's tags with keyword-based fallbacks.
- **Inspect an agent's configuration** — clicking any card opens a popup with the
  full config snapshot: model, instructions, tags, capabilities, MCP endpoints,
  tools, metadata, authority, and Foundry surface.

## Run

```powershell
cd samples/agent-mesh-platform
node server.js
```

Then open <http://localhost:3100>.

## Configuration

Environment variables:

| Var                | Default                                                       | Purpose                                 |
| ------------------ | ------------------------------------------------------------- | --------------------------------------- |
| `PORT`             | `3100`                                                        | Port for this dev server                |
| `ORCHESTRATOR_URL` | `http://orchestrator-engine.eastus.azurecontainer.io:8080`    | Base URL of the .NET OrchestratorEngine |

The server proxies any request beginning with `/api/` to `ORCHESTRATOR_URL`; all
other requests serve static assets from `./public`.

To point the sample at a local API instead, run:

```powershell
$env:ORCHESTRATOR_URL = "http://localhost:5144"
node server.js
```

## APIs used

- `GET  /api/orchestrator/health` — status pill in the top bar.
- `GET  /api/orchestrator/agents` — catalog (returns `AgentInfo[]` with a rich
  `configuration` snapshot).
- `POST /api/orchestrator/onboard` — creates a new Foundry agent.
