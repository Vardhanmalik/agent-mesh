# Copilot Sample UI — Agent Mesh

A local, Copilot-style web app that mirrors the **Agent Mesh** prototype
(from `agent_mesh/templates/starter/app`), rebuilt in vanilla HTML/CSS/JS so
it drops in next to the **OrchestratorEngine.Api** without a build step.

The transcript is **scripted mock data** — identical strings, agents, picks,
prices, and animations as the React prototype. The API health dot at the
bottom-left still pings `/api/orchestrator/health` so you can see whether the
real backend is reachable, but no `/discover` / `/execute` calls are made in
this build (per the "keep the backend as-is" scope).

Zero runtime dependencies. Node is used only as a static file server + a tiny
reverse proxy so `/api/*` reaches the .NET backend without CORS gymnastics.

## What it looks like

- Left sidebar: brand, "New chat", seeded chats list (Laptop / Pizza), user ID, API health dot
- Welcome screen with two scenario cards (Laptop, Pizza)
- Orchestrated chat: user prompt → Copilot analysis → three brand-agent cards each with picks
- Per-pick one-tap **Buy** (laptop) / **Order** (pizza) → purchase confirmation → order tracking
- **Chat with agent** drops an `@[agent]` tag into the composer and continues in the same thread
- Scripted follow-up chip + **Is there a discount?** chip; discount reply renders a re-priced card
- Toast notifications for completed orders
- Composer with autosizing textarea, Enter to send, Shift+Enter for newline

## Folder layout

```
samples/copilot-sample-ui/
├─ package.json          # no runtime deps, just a start script
├─ server.js             # static server + /api/* proxy
├─ README.md
└─ public/
   ├─ index.html
   ├─ styles.css
   ├─ app.js
   └─ assets/
      └─ agent-mesh/     # copied from agent_mesh/.../assets/agent-mesh
         ├─ *.jpg        # laptop + pizza + brand hero photos
         └─ logos/*.svg  # Simple Icons brand marks
```

## Run it

By default the sample UI's health dot pings the **Azure-hosted orchestrator**
at `http://orchestrator-engine.eastus.azurecontainer.io:8080`. That's just for
the "API connected" dot — the transcript itself is scripted, so the UI works
end-to-end even if the backend is unreachable.

### Start the sample UI

```powershell
cd samples/copilot-sample-ui
node server.js
```

Open <http://localhost:3000>, then click **Find a gaming laptop** or
**Order pizza tonight** to seed a scenario.

### Point the health dot at a local API instead

```powershell
# terminal 1
cd src/OrchestratorEngine.Api
dotnet run --configuration Development

# terminal 2
cd samples/copilot-sample-ui
$env:ORCHESTRATOR_URL = "http://localhost:5144"
node server.js
```

## Configuration

Env vars read by [server.js](server.js):

| Var                 | Default                                                         | Purpose                                    |
| ------------------- | --------------------------------------------------------------- | ------------------------------------------ |
| `PORT`              | `3000`                                                          | Port for the sample UI dev server          |
| `ORCHESTRATOR_URL`  | `http://orchestrator-engine.eastus.azurecontainer.io:8080`      | Base URL of the OrchestratorEngine.Api (used only by the `/api/*` proxy and the health dot) |

## Flow reference — what the backend would return to reproduce this UI

Even though this sample is scripted, it's the reference shape for how the
OrchestratorEngine should structure its `/discover` and `/execute` responses
if/when it drives this UI for real:

| UI action                          | Would-be endpoint                    | Payload shape (see [`public/app.js`](public/app.js) `AGENT_MESH_SCENARIOS`) |
| ---------------------------------- | ------------------------------------ | -------------------------------------------------------------------------- |
| Pick a scenario suggestion         | `POST /api/orchestrator/discover`    | Response includes `analysis` string + 3 `agents`, each with `pitch`, `whyPicked`, `handoff` (chip + reply), and 3 `picks` (`title`, `detail`, `price`, `personalizationTag?`, `moreInfo`) |
| Click **Buy** / **Order** on a pick | `POST /api/orchestrator/execute`    | `optionId` = pick id; response drives the purchase confirmation card (address, payment, total) |
| Click **Pay …** on confirmation    | `POST /api/orchestrator/confirm`     | Response includes `orderNumber` + `statusLabel` for the tracking card       |
| Click **Chat with agent**          | Same-thread continuation              | Frontend adds `@[agent]` tag; follow-ups POST to `/execute` with the tagged `agentId` on the existing `sessionId` |
| Scripted **Is there a discount?**  | Same-thread continuation              | Response includes re-priced picks (client applies flat 15% off in the mock) |

Nothing in this UI blocks on any of those calls today — the scripted flow
gives designers something to iterate on before the backend catches up.

## Customization tips

- **Scenario copy:** edit the `AGENT_MESH_SCENARIOS` array near the top of
  [public/app.js](public/app.js) — analysis text, agents, pitches, and picks
  are all authored strings.
- **Brand images / logos:** drop replacements into
  [public/assets/agent-mesh](public/assets/agent-mesh) using the same file
  names, or add entries to `LAPTOP_PICK_IMAGES` / `PIZZA_PICK_IMAGES` /
  `AGENT_LOGOS`.
- **Colors:** tweak the `--brand-1|2|3` and `--brand-primary` CSS variables at
  the top of [public/styles.css](public/styles.css).
- **Purchase timings:** adjust `FETCH_DELAY_MS`, `PROCESSING_DELAY_MS`,
  `SUCCESS_DELAY_MS`, `THINKING_DELAY_MS` in [public/app.js](public/app.js).

## Security note

This server is intentionally minimal and meant for **local development only**.
It has no auth, no rate limiting, and forwards every `/api/*` request verbatim
to the configured backend. Do not expose it publicly.
