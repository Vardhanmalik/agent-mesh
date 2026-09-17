# Edge + Copilot extension — Sample UI

A local, Microsoft Edge–styled web app that mocks a **Copilot browser extension**
sitting in the toolbar. Clicking the extension:

1. Reads the current tab URL from the address bar
2. Classifies the host into a domain (food delivery / shopping / travel / dining)
3. Sends a **generic prompt** for that domain to the existing
   `POST /api/orchestrator/discover` endpoint
4. Renders the **top 3 agent options** in the extension side panel
5. Falls back to **“Agent not found”** if the host isn't registered (or the
   orchestrator returns no options)

This is layered on top of the existing `/discover` API — **no server-side
changes required**. It's a companion sample to the M365 Copilot sample under
[`samples/copilot-sample-ui`](../copilot-sample-ui/README.md).

## What it looks like

- Edge-style title bar with real-looking tabs and window controls
- Toolbar with back/forward/reload, address bar, favorites + **Copilot** button
- Simulated web page render for the active tab (host-aware hero + cards)
- Copilot side panel that slides in on click, with:
  - Detected host + inferred domain chip
  - Prompt override textarea (blank ⇒ uses the domain default prompt)
  - Top 3 agent option cards with `Choose` / `Confirm` actions

## Folder layout

```
samples/edge-copilot-sample-ui/
├─ package.json          # no runtime deps
├─ server.js             # static server + /api/* proxy (mirrors M365 sample)
├─ README.md
└─ public/
   ├─ index.html
   ├─ styles.css
   └─ app.js             # tab model, host registry, /discover call
```

## Run it

By default the sample UI talks to the same Azure-hosted orchestrator as the
M365 Copilot sample — you do **not** need to run the .NET API locally.

```powershell
cd samples/edge-copilot-sample-ui
node server.js
```

Open <http://localhost:3100>.

You should see:

- Three preloaded tabs (DoorDash, Amazon, Expedia)
- `API connected` in the Copilot panel header once you open it
- A pulsing **Copilot** button in the toolbar

### Point at a local API

```powershell
# terminal 1
cd src/OrchestratorEngine.Api
dotnet run --configuration Development

# terminal 2
cd samples/edge-copilot-sample-ui
$env:ORCHESTRATOR_URL = "http://localhost:5144"
node server.js
```

### Run alongside the M365 sample

Both samples default to a different port so you can run them side-by-side:

| Sample                                     | Default port |
| ------------------------------------------ | ------------ |
| [`copilot-sample-ui`](../copilot-sample-ui/) (M365 Copilot chat) | `3000`       |
| `edge-copilot-sample-ui` (Edge extension)  | `3100`       |

## Configuration

Env vars read by [server.js](server.js):

| Var                 | Default                                                       | Purpose                                    |
| ------------------- | ------------------------------------------------------------- | ------------------------------------------ |
| `PORT`              | `3100`                                                        | Port for this dev server                   |
| `ORCHESTRATOR_URL`  | `http://orchestrator-engine.eastus.azurecontainer.io:8080`    | Base URL of the OrchestratorEngine.Api     |

## How the flow maps to the API

| UI action                                      | Endpoint                          | Payload highlights                                                                 |
| ---------------------------------------------- | --------------------------------- | ---------------------------------------------------------------------------------- |
| Click the toolbar **Copilot** button           | `POST /api/orchestrator/discover` | `userId`, generated `prompt` (from host registry), `context.host`, `context.siteDomain` |
| Click **Choose** on one of the top-3 options   | `POST /api/orchestrator/execute`  | `userId`, `sessionId`, `optionId`                                                  |
| Click **Confirm** on one of the top-3 options  | `POST /api/orchestrator/confirm`  | `userId`, `sessionId`, `optionId`, `prompt: "Please confirm this booking."`        |
| Health pill in the Copilot panel header        | `GET  /api/orchestrator/health`   | Polled every 20s                                                                   |

The `sessionId` returned by the first `/discover` call is threaded through
subsequent `/execute` and `/confirm` calls, exactly like the M365 sample.
Changing the address bar to a new URL drops the `sessionId` (fresh page →
fresh conversation).

## Host → domain registry

The host classifier lives in `public/app.js` in the `HOST_REGISTRY` constant.
Each entry defines the domain label, favicon glyph, hero gradient, and — most
importantly — the **default generic prompt** sent to `/discover` when the user
doesn't override it in the panel textarea.

Registered hosts today:

| Domain          | Hosts                                                                |
| --------------- | -------------------------------------------------------------------- |
| food delivery   | doordash.com, ubereats.com, swiggy.com, zomato.com, grubhub.com       |
| shopping        | amazon.com, walmart.com, target.com, ebay.com, myntra.com, flipkart.com, bestbuy.com |
| travel          | expedia.com, kayak.com, booking.com, airbnb.com                       |
| dining          | opentable.com, resy.com                                               |

Anything else → the panel shows **“Agent not found.”** with a hint to try one
of the preset sites via the `⌄` button in the address bar.

### Overriding the prompt

If the user types their own prompt in the panel textarea before clicking
**Ask Copilot**, that prompt is used verbatim. Otherwise the domain default
from the registry is sent. Example (Amazon):

```
Buy me 3 pairs of running shoes under $100 with prime shipping.
```

### Adding a new host

Add an entry to `HOST_REGISTRY` in [public/app.js](public/app.js):

```js
'newsite.com': makeEntry(
    'food delivery',           // domain label
    'food',                    // chip css class (food|shop|travel|dine)
    'N',                       // favicon glyph letter
    '#ff5722', '#b23c17',      // hero gradient stops
    'Order me a family dinner combo under $30.'
),
```

## Security note

This server is intentionally minimal and meant for **local development only**.
It has no auth, no rate limiting, and forwards every `/api/*` request verbatim
to the configured backend. Do not expose it publicly.
