using Azure.Identity;
using Microsoft.Extensions.Options;
using OrchestratorEngine.Api.Configuration;
using OrchestratorEngine.Api.Models;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace OrchestratorEngine.Api.Services;

public sealed class FoundryAgentService : IFoundryAgentService
{
    // Static cache so surface routing survives across HttpClient-scoped instances.
    // Populated by ListAvailableAgentsAsync; consulted by InvokeAgentAsync.
    private static readonly ConcurrentDictionary<string, string> AgentSurfaceCache = new(StringComparer.Ordinal);

    // v1 hosted-agents expose their invocation URL via `agent_endpoint` on the agent definition
    // (when present) — otherwise the URL is derivable by convention from the agent id (see
    // ResolveAgentEndpointAsync). Cached to avoid re-computing on every invoke.
    private static readonly ConcurrentDictionary<string, string> AgentEndpointCache = new(StringComparer.Ordinal);

    private readonly HttpClient _httpClient;
    private readonly FoundryOptions _options;
    private readonly ILogger<FoundryAgentService> _logger;

    public FoundryAgentService(
        HttpClient httpClient,
        IOptions<FoundryOptions> options,
        ILogger<FoundryAgentService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<List<AgentInfo>> ListAvailableAgentsAsync(CancellationToken ct = default)
    {
        var endpoint = _options.Endpoint.TrimEnd('/');
        var agents = new List<AgentInfo>();

        // Query BOTH Foundry data-plane surfaces because portal-created agents may live under either:
        //   1. OpenAI-Assistants-compatible: /assistants?api-version=<preview>
        //   2. Foundry Agent Service v1 GA (hosted agents / Microsoft Agent Framework): /agents?api-version=v1
        // Some tenants surface an agent only under one; some under both. We aggregate and dedupe by id.
        //
        // IMPORTANT: both surfaces use OpenAI-style cursor pagination (`has_more` + `last_id`).
        // The /assistants list defaults to limit=20 with no auto-pagination, so any project with
        // 20+ assistants would drop newly-onboarded agents off the tail — that's the bug where
        // a freshly-onboarded flight agent was never fanned out to because AirIndia + Emirates
        // (and older assistants) already filled the first page. We now request the max page size
        // (limit=100) on both surfaces AND follow `has_more`/`last_id` until the tenant is fully
        // enumerated (bounded by MaxAgentPages as a safety net).
        await TryFetchAgentsAsync(
            $"{endpoint}/assistants?api-version={_options.ApiVersion}&limit=100&order=desc",
            surface: "assistants",
            agents,
            ct);

        await TryFetchAgentsAsync(
            $"{endpoint}/agents?api-version=v1&limit=100&order=desc",
            surface: "agents-v1",
            agents,
            ct);

        var deduped = agents
            .Where(a => !string.IsNullOrEmpty(a.AgentId))
            .GroupBy(a => a.AgentId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        if (deduped.Count == 0)
        {
            _logger.LogWarning(
                "Foundry ListAgents returned 0 agents across BOTH /assistants and /agents?api-version=v1. " +
                "Endpoint={Endpoint}. Verify: (a) the endpoint points to the exact Foundry project that owns the agents, " +
                "(b) the identity used by DefaultAzureCredential has 'Azure AI User' (or equivalent) RBAC on that project, " +
                "(c) the agents were created in this project (not another project in the same tenant).",
                endpoint);
        }
        else
        {
            _logger.LogInformation("Found {Count} agents in Foundry (aggregated across surfaces)", deduped.Count);

            // Name-level dump so we can prove *at the source* whether a specific onboarded agent
            // (e.g. a freshly-created Indigo Flight Agent on the /assistants surface) is being
            // returned by the list APIs. Without this, downstream drops (domain filter, ranker,
            // fan-out cap) all look identical in the log — you can't tell whether the agent was
            // filtered out or never reached the pipeline in the first place.
            foreach (var a in deduped)
            {
                _logger.LogInformation(
                    "ListAgents result: surface={Surface} id={AgentId} name='{Name}' capabilities=[{Caps}]",
                    a.Surface, a.AgentId, a.Name, string.Join(",", a.Capabilities));
            }
        }

        return deduped;
    }

    // Safety cap on paginated list. With limit=100 this allows up to 1,000 agents per surface
    // before we stop following `has_more`, which is well beyond any realistic Foundry project
    // while still guaranteeing termination if the API ever returns a broken cursor.
    private const int MaxAgentPages = 10;

    private async Task TryFetchAgentsAsync(
        string baseUrl, string surface, List<AgentInfo> sink, CancellationToken ct)
    {
        var startCount = sink.Count;
        string? afterCursor = null;

        for (var page = 0; page < MaxAgentPages; page++)
        {
            var url = string.IsNullOrEmpty(afterCursor)
                ? baseUrl
                : $"{baseUrl}&after={Uri.EscapeDataString(afterCursor)}";

            _logger.LogInformation(
                "Fetching Foundry {Surface} page {Page} at {Url}", surface, page, url);

            HttpResponseMessage response;
            string content;
            try
            {
                response = await _httpClient.GetAsync(url, ct);
                content = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Foundry {Surface} query threw. Url={Url}", surface, url);
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Foundry {Surface} query failed. HttpStatus={HttpStatus} Url={Url} Body={Body}",
                    surface, (int)response.StatusCode, url, Truncate(content, 500));
                return;
            }

            var (addedThisPage, hasMore, lastId) = ParseAgentsPage(content, surface, sink);

            _logger.LogInformation(
                "Foundry {Surface} page {Page}: added {Count} agent(s), hasMore={HasMore}, lastId={LastId}.",
                surface, page, addedThisPage, hasMore, string.IsNullOrEmpty(lastId) ? "-" : lastId);

            if (addedThisPage == 0 && page == 0)
            {
                _logger.LogDebug("Foundry {Surface} empty body: {Body}", surface, Truncate(content, 500));
            }

            if (!hasMore || string.IsNullOrEmpty(lastId))
                break;

            afterCursor = lastId;
        }

        var totalAdded = sink.Count - startCount;
        _logger.LogInformation(
            "Foundry {Surface} enumeration complete. TotalAgents={Count}.", surface, totalAdded);
    }

    /// <summary>
    /// Parse one page of a Foundry list response. Returns (agents added to sink, has_more, last_id).
    /// Accepts <c>data</c> (OpenAI Assistants + Agents-v1 GA), <c>value</c> (Azure ARM style),
    /// or a bare array at the root.
    /// </summary>
    private (int Added, bool HasMore, string LastId) ParseAgentsPage(
        string content, string surface, List<AgentInfo> sink)
    {
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        JsonElement array = default;
        var hasArray = false;
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root; hasArray = true;
        }
        else if (root.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
        {
            array = dataArr; hasArray = true;
        }
        else if (root.TryGetProperty("value", out var valueArr) && valueArr.ValueKind == JsonValueKind.Array)
        {
            array = valueArr; hasArray = true;
        }

        var added = 0;
        if (!hasArray) return (added, false, string.Empty);

        foreach (var el in array.EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrEmpty(id)) continue;

            var configuration = ExtractConfiguration(el, surface);
            var mcpEndpoint = configuration.TryGetValue("mcp_server_url", out var mcpVal)
                ? mcpVal as string ?? string.Empty
                : string.Empty;

            sink.Add(new AgentInfo
            {
                AgentId = id,
                Name = el.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString() ?? string.Empty : string.Empty,
                FriendlyId = AgentInfo.BuildFriendlyId(
                    el.TryGetProperty("name", out var nameEl2) && nameEl2.ValueKind == JsonValueKind.String
                        ? nameEl2.GetString() : null),
                Description = el.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String
                    ? desc.GetString() ?? string.Empty : string.Empty,
                Provider = el.TryGetProperty("metadata", out var meta)
                    && meta.ValueKind == JsonValueKind.Object
                    && meta.TryGetProperty("provider", out var prov)
                    && prov.ValueKind == JsonValueKind.String
                    ? prov.GetString() ?? "azure-foundry" : "azure-foundry",
                Capabilities = ExtractCapabilities(el),
                McpServerEndpoint = mcpEndpoint,
                Configuration = configuration,
                Surface = surface
            });

            // Remember which surface this agent lives on so Invoke can pick the right runtime.
            AgentSurfaceCache[id] = surface;

            // For v1 hosted agents, capture agent_endpoint so InvokeAgentAsync can POST there
            // without an extra round-trip. On the /assistants surface this field is absent.
            if (surface == "agents-v1")
            {
                var agentEndpoint = TryExtractAgentEndpoint(el);
                if (!string.IsNullOrWhiteSpace(agentEndpoint))
                {
                    AgentEndpointCache[id] = agentEndpoint;
                }
                else
                {
                    // Dump the FULL definition (not a short preview) — v1 hosted agents nest
                    // their runtime config under versions.latest.* and we need to see every
                    // field to route correctly. Truncated previews hide exactly the properties
                    // we care about (voice-live.configuration, chat_endpoint, connection, …).
                    _logger.LogInformation(
                        "v1 hosted-agent {AgentId}: no invocation endpoint found by recursive scan. FULL definition: {Definition}",
                        id, Truncate(el.GetRawText(), 8000));
                }
            }

            added++;
        }

        // Cursor bookkeeping: OpenAI-style list responses expose `has_more` + `last_id` at the
        // root level (both surfaces). If either field is missing we stop paginating and rely on
        // whatever we've already collected.
        var hasMore = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("has_more", out var hmEl)
            && hmEl.ValueKind == JsonValueKind.True;

        var lastId = string.Empty;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("last_id", out var lidEl)
            && lidEl.ValueKind == JsonValueKind.String)
        {
            lastId = lidEl.GetString() ?? string.Empty;
        }

        // Some tenants omit `last_id` from the envelope but the cursor is still derivable from
        // the id of the last item in the returned page. Fall back to that so a well-formed page
        // with `has_more=true` doesn't strand us without a way to advance.
        if (hasMore && string.IsNullOrEmpty(lastId) && array.GetArrayLength() > 0)
        {
            var last = array[array.GetArrayLength() - 1];
            if (last.TryGetProperty("id", out var lastIdEl) && lastIdEl.ValueKind == JsonValueKind.String)
                lastId = lastIdEl.GetString() ?? string.Empty;
        }

        return (added, hasMore, lastId);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty : (s.Length > max ? s[..max] + "…" : s);

    public async Task<AgentInfo?> GetAgentAsync(string agentId, CancellationToken ct = default)
    {
        var requestUrl = $"{_options.Endpoint.TrimEnd('/')}/assistants/{agentId}?api-version={_options.ApiVersion}";

        var response = await _httpClient.GetAsync(requestUrl, ct);
        if (!response.IsSuccessStatusCode) return null;

        var content = await response.Content.ReadAsStringAsync(ct);
        var element = JsonDocument.Parse(content).RootElement;

        return new AgentInfo
        {
            AgentId = element.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty,
            Name = element.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                ? nameEl.GetString() ?? string.Empty : string.Empty,
            FriendlyId = AgentInfo.BuildFriendlyId(
                element.TryGetProperty("name", out var nameEl2) && nameEl2.ValueKind == JsonValueKind.String
                    ? nameEl2.GetString() : null),
            Description = element.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String
                ? desc.GetString() ?? string.Empty : string.Empty,
            Provider = element.TryGetProperty("metadata", out var meta)
                && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("provider", out var prov)
                && prov.ValueKind == JsonValueKind.String
                ? prov.GetString() ?? "azure-foundry" : "azure-foundry"
        };
    }

    public async Task<AgentInfo> CreateAgentAsync(OnboardAgentRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Name is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Description))
            throw new ArgumentException("Description is required.", nameof(request));

        var endpoint = _options.Endpoint.TrimEnd('/');
        var url = $"{endpoint}/assistants?api-version={_options.ApiVersion}";

        // Model resolution order:
        //   1. Caller-supplied request.Model (highest priority — lets the onboarding UI override).
        //   2. FoundryOptions.DefaultAgentModel from config — the recommended way to point at
        //      whichever deployment actually exists in the target Foundry project.
        //   3. Hardcoded "gpt-4o-mini" fallback (kept only for backward compatibility with
        //      existing deployments that already have that deployment name provisioned).
        // If steps 2 and 3 don't match an existing Foundry model deployment, run invocations
        // will fail with last_error.code=invalid_engine_error / "Failed to resolve model info".
        var model = !string.IsNullOrWhiteSpace(request.Model)
            ? request.Model.Trim()
            : !string.IsNullOrWhiteSpace(_options.DefaultAgentModel)
                ? _options.DefaultAgentModel.Trim()
                : "gpt-5-mini";

        // Synthesize default instructions when the caller doesn't supply any. The default
        // matches BuildEnrichedPrompt's contract so ranker/JSON parsing keeps working.
        var instructions = string.IsNullOrWhiteSpace(request.Instructions)
            ? $"You are {request.Name}. {request.Description}\n\n" +
              "When responding to a user request, return 2-3 concrete offers as a JSON array " +
              "wrapped in ```json``` fences. Each item MUST include: title, description, price " +
              "(number), currency. Do not add prose before or after the JSON block. Do not ask " +
              "clarifying questions — assume sensible defaults and note them in the description."
            : request.Instructions.Trim();

        // Assemble metadata. Foundry metadata values must be strings; join list values with commas.
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider"] = "orchestrator-onboard"
        };
        if (request.Tags.Count > 0)
            metadata["tags"] = string.Join(",", request.Tags);
        if (request.Capabilities.Count > 0)
            metadata["capabilities"] = string.Join(",", request.Capabilities);
        if (!string.IsNullOrWhiteSpace(request.Authority))
            metadata["authority"] = request.Authority!;
        if (!string.IsNullOrWhiteSpace(request.McpServerUrl))
            metadata["mcp_server_url"] = request.McpServerUrl!;
        if (!string.IsNullOrWhiteSpace(request.McpServerUrlDiscovery))
            metadata["mcp_server_url_discovery"] = request.McpServerUrlDiscovery!;
        if (!string.IsNullOrWhiteSpace(request.McpServerUrlExecution))
            metadata["mcp_server_url_execution"] = request.McpServerUrlExecution!;
        if (!string.IsNullOrWhiteSpace(request.McpServerUrlConfirmation))
            metadata["mcp_server_url_confirmation"] = request.McpServerUrlConfirmation!;
        foreach (var kv in request.Metadata)
        {
            if (!string.IsNullOrWhiteSpace(kv.Key) && !metadata.ContainsKey(kv.Key))
                metadata[kv.Key] = kv.Value ?? string.Empty;
        }

        // Treat any supplied MCP URL(s) as *informational* reference endpoints, NOT as live
        // Foundry `mcp` tool wiring. Historically we registered them as tools[type=mcp] on
        // /assistants, but Foundry's runtime consistently failed those runs (last_error on
        // the run object) because the /assistants surface at api-version 2025-05-15-preview
        // does not reliably invoke remote MCP servers. Instead we now:
        //   1. Persist the URLs in metadata (already done above) so orchestration lookup works.
        //   2. Append them as a plain-text "Reference endpoints" block to the agent's
        //      instructions, so the model is aware of them as URLs it can *describe* — same
        //      way it would treat any other website mentioned in its system prompt.
        // The user's `Instructions` field remains the primary contract for "what the agent
        // is expected to do" and is passed through to Foundry's `instructions` field verbatim
        // (or with the default JSON-offer contract when the caller omits it).
        var referenceUrls = new List<(string Label, string Url)>();
        if (!string.IsNullOrWhiteSpace(request.McpServerUrl))
            referenceUrls.Add(("primary", request.McpServerUrl!));
        if (!string.IsNullOrWhiteSpace(request.McpServerUrlDiscovery))
            referenceUrls.Add(("discovery", request.McpServerUrlDiscovery!));
        if (!string.IsNullOrWhiteSpace(request.McpServerUrlExecution))
            referenceUrls.Add(("execution", request.McpServerUrlExecution!));
        if (!string.IsNullOrWhiteSpace(request.McpServerUrlConfirmation))
            referenceUrls.Add(("confirmation", request.McpServerUrlConfirmation!));

        if (referenceUrls.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(instructions);
            sb.Append("\n\nReference endpoints (informational only — treat these as URLs you may cite, not tools you can call):\n");
            foreach (var (label, u) in referenceUrls)
                sb.Append("- ").Append(label).Append(": ").Append(u).Append('\n');
            instructions = sb.ToString().TrimEnd();
        }

        var body = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["name"] = request.Name,
            ["description"] = request.Description,
            ["instructions"] = instructions,
            ["metadata"] = metadata
        };

        _logger.LogInformation(
            "Onboarding Foundry agent '{Name}' via {Url} (model={Model}, referenceUrls={RefCount})",
            request.Name, url, model, referenceUrls.Count);

        HttpResponseMessage response;
        string content;
        try
        {
            response = await _httpClient.PostAsJsonAsync(url, body, ct);
            content = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Foundry CreateAgent POST threw. Name={Name} Url={Url}", request.Name, url);
            throw new InvalidOperationException(
                $"Foundry CreateAgent transport error: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Foundry CreateAgent failed. Status={Status} Name={Name} Body={Body}",
                (int)response.StatusCode, request.Name, Truncate(content, 500));
            throw new InvalidOperationException(
                $"Foundry CreateAgent returned HTTP {(int)response.StatusCode}: {Truncate(content, 500)}");
        }

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        var agentId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString() ?? string.Empty : string.Empty;

        if (string.IsNullOrEmpty(agentId))
        {
            throw new InvalidOperationException(
                $"Foundry CreateAgent succeeded but returned no id. Body={Truncate(content, 500)}");
        }

        // Warm caches so a subsequent /discover fan-out or /execute call routes correctly
        // without waiting for the next ListAvailableAgents refresh.
        AgentSurfaceCache[agentId] = "assistants";

        var info = new AgentInfo
        {
            AgentId = agentId,
            FriendlyId = AgentInfo.BuildFriendlyId(request.Name),
            Name = request.Name,
            Description = request.Description,
            Provider = "orchestrator-onboard",
            Capabilities = [.. request.Capabilities],
            McpServerEndpoint = request.McpServerUrl ?? string.Empty,
            Configuration = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["model"] = model,
                ["tags"] = request.Tags,
                ["authority"] = request.Authority ?? string.Empty,
                ["mcp_server_url_discovery"] = request.McpServerUrlDiscovery ?? string.Empty,
                ["mcp_server_url_execution"] = request.McpServerUrlExecution ?? string.Empty,
                ["mcp_server_url_confirmation"] = request.McpServerUrlConfirmation ?? string.Empty
            },
            Surface = "assistants"
        };

        _logger.LogInformation(
            "Onboarded Foundry agent '{Name}' with id {AgentId} on surface=assistants.",
            request.Name, agentId);

        return info;
    }

    private static string Slugify(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "agent";
        var sb = new System.Text.StringBuilder(s.Length);
        var lastDash = false;
        foreach (var ch in s.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }
        return sb.ToString().Trim('-');
    }

    public async Task<Dictionary<string, object>> InvokeAgentAsync(
        string agentId,
        string prompt,
        Dictionary<string, object>? parameters = null,
        string? threadId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("agentId is required.", nameof(agentId));

        var endpoint = _options.Endpoint.TrimEnd('/');

        // Route by cached surface.
        //   `assistants` and v1-GA persistent agents share the OpenAI-Assistants `/threads/runs`
        //   runtime (assistants uses the preview api-version; the GA runtime uses api-version=v1).
        //   Both require `assistant_id` with an `asst_*` shaped id.
        //
        //   v1 hosted/connected agents (e.g. portal-created flight agents) do NOT share that
        //   runtime — their id is a friendly name and each agent carries its own `agent_endpoint`.
        //   For those we POST directly to the per-agent endpoint and short-circuit the assistants
        //   runtime entirely.
        var surface = AgentSurfaceCache.TryGetValue(agentId, out var s) ? s : "assistants";
        if (surface == "agents-v1")
        {
            return await InvokeV1HostedAgentAsync(agentId, prompt, parameters, ct);
        }

        var apiVersion = _options.ApiVersion;
        var apiVersionQuery = $"api-version={apiVersion}";

        // Step 1: Create (or continue) the run.
        //   Fresh conversation: POST {endpoint}/threads/runs?api-version=...
        //     { "assistant_id": "...", "thread": { "messages": [ {role, content} ] } }
        //   Continuation:      POST {endpoint}/threads/{tid}/messages?api-version=...  (add user turn)
        //                      POST {endpoint}/threads/{tid}/runs?api-version=...      { "assistant_id": "..." }
        string createRunUrl;
        object createBody;
        var isContinuation = !string.IsNullOrWhiteSpace(threadId);

        if (isContinuation)
        {
            var addMsgUrl = $"{endpoint}/threads/{threadId}/messages?{apiVersionQuery}";
            var addMsgResp = await _httpClient.PostAsJsonAsync(addMsgUrl,
                new { role = "user", content = prompt }, ct);
            var addMsgBody = await addMsgResp.Content.ReadAsStringAsync(ct);
            if (!addMsgResp.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Foundry add-message failed. Status={Status} AgentId={AgentId} Thread={Thread} Body={Body}",
                    (int)addMsgResp.StatusCode, agentId, threadId, addMsgBody);
                addMsgResp.EnsureSuccessStatusCode();
            }

            createRunUrl = $"{endpoint}/threads/{threadId}/runs?{apiVersionQuery}";
            var runBody = new Dictionary<string, object> { ["assistant_id"] = agentId };
            if (parameters is { Count: > 0 })
                runBody["additional_instructions"] = JsonSerializer.Serialize(parameters);
            createBody = runBody;
        }
        else
        {
            createRunUrl = $"{endpoint}/threads/runs?{apiVersionQuery}";
            var runBody = new Dictionary<string, object>
            {
                ["assistant_id"] = agentId,
                ["thread"] = new
                {
                    messages = new[] { new { role = "user", content = prompt } }
                }
            };
            if (parameters is { Count: > 0 })
                runBody["additional_instructions"] = JsonSerializer.Serialize(parameters);
            createBody = runBody;
        }

        _logger.LogInformation(
            "Invoking Foundry agent {AgentId} on surface={Surface} via {Url} (continuation={Cont})",
            agentId, surface, createRunUrl, isContinuation);

        var createResponse = await _httpClient.PostAsJsonAsync(createRunUrl, createBody, ct);
        var createContent = await createResponse.Content.ReadAsStringAsync(ct);
        if (!createResponse.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Foundry create-run failed. Status={Status} AgentId={AgentId} Surface={Surface} Url={Url} Body={Body}",
                (int)createResponse.StatusCode, agentId, surface, createRunUrl, createContent);

            // Return a structured error instead of throwing so fan-out doesn't collapse when a
            // single agent misbehaves.
            return new Dictionary<string, object>
            {
                ["agentId"] = agentId,
                ["provider"] = "azure-foundry",
                ["runId"] = string.Empty,
                ["threadId"] = threadId ?? string.Empty,
                ["status"] = "create_failed",
                ["surface"] = surface,
                ["summary"] = $"Agent invocation failed with HTTP {(int)createResponse.StatusCode}.",
                ["content"] = string.Empty,
                ["error"] = Truncate(createContent, 500)
            };
        }

        string runId, effectiveThreadId, status;
        string? lastErrorCode = null;
        string? lastErrorMessage = null;
        using (var createDoc = JsonDocument.Parse(createContent))
        {
            var root = createDoc.RootElement;
            runId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() ?? string.Empty : string.Empty;
            effectiveThreadId = root.TryGetProperty("thread_id", out var tidEl) && tidEl.ValueKind == JsonValueKind.String
                ? tidEl.GetString() ?? string.Empty
                : threadId ?? string.Empty;
            status = root.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String
                ? stEl.GetString() ?? string.Empty : string.Empty;
            (lastErrorCode, lastErrorMessage) = ExtractRunLastError(root);
        }

        // Step 2: Poll the run until it reaches a terminal state (or we hit the deadline).
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        var delay = TimeSpan.FromMilliseconds(500);
        while (!string.IsNullOrEmpty(effectiveThreadId)
               && !string.IsNullOrEmpty(runId)
               && !IsTerminalRunStatus(status)
               && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 3000));

            var pollUrl = $"{endpoint}/threads/{effectiveThreadId}/runs/{runId}?{apiVersionQuery}";
            var pollResponse = await _httpClient.GetAsync(pollUrl, ct);
            var pollContent = await pollResponse.Content.ReadAsStringAsync(ct);
            if (!pollResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Foundry poll-run failed. HttpStatus={HttpStatus} AgentId={AgentId} Body={Body}",
                    (int)pollResponse.StatusCode, agentId, pollContent);
                break;
            }
            using var pollDoc = JsonDocument.Parse(pollContent);
            var pollRoot = pollDoc.RootElement;
            status = pollRoot.TryGetProperty("status", out var statusEl2) && statusEl2.ValueKind == JsonValueKind.String
                ? statusEl2.GetString() ?? status : status;
            // Refresh last_error on every poll — Foundry only populates it once the run reaches
            // a failed/cancelled/expired terminal state, so the *final* poll is where the
            // authoritative payload lands. Keep whichever poll last returned a non-empty pair
            // so we don't overwrite a real error with a null from an interim in-progress poll.
            var (code, message) = ExtractRunLastError(pollRoot);
            if (!string.IsNullOrEmpty(code) || !string.IsNullOrEmpty(message))
            {
                lastErrorCode = code;
                lastErrorMessage = message;
            }
        }

        // Step 3: If run completed, fetch the last assistant message text for the summary.
        var summary = string.Empty;
        if (status == "completed" && !string.IsNullOrEmpty(effectiveThreadId))
        {
            var msgUrl = $"{endpoint}/threads/{effectiveThreadId}/messages?{apiVersionQuery}&order=desc&limit=10";
            var msgResponse = await _httpClient.GetAsync(msgUrl, ct);
            var msgContent = await msgResponse.Content.ReadAsStringAsync(ct);
            if (msgResponse.IsSuccessStatusCode)
            {
                summary = ExtractFirstAssistantText(msgContent);
            }
            else
            {
                _logger.LogWarning(
                    "Foundry list-messages failed. HttpStatus={HttpStatus} Thread={Thread} Body={Body}",
                    (int)msgResponse.StatusCode, effectiveThreadId, msgContent);
            }
        }

        // Terminal-state diagnostics. For failed/expired/cancelled/incomplete runs the Foundry
        // run object carries a `last_error: { code, message }` payload that is the single most
        // useful piece of information for the caller — e.g. `server_error`,
        // `tool_output_required`, `invalid_tool_config` for MCP tool misconfiguration. Without
        // surfacing it, a failed run is indistinguishable from a silent no-op.
        if (status is "failed" or "expired" or "cancelled" or "incomplete")
        {
            _logger.LogError(
                "Foundry agent {AgentId} run {RunId} ended with status={Status} on surface={Surface}. " +
                "last_error.code={ErrCode} last_error.message={ErrMsg}",
                agentId, runId, status, surface,
                lastErrorCode ?? "(none)", lastErrorMessage ?? "(none)");
        }
        else
        {
            _logger.LogInformation(
                "Foundry agent {AgentId} run {RunId} ended with status={Status} on surface={Surface}",
                agentId, runId, status, surface);
        }

        var result = new Dictionary<string, object>
        {
            ["agentId"] = agentId,
            ["provider"] = "azure-foundry",
            ["runId"] = runId,
            ["threadId"] = effectiveThreadId,
            ["status"] = status,
            ["surface"] = surface,
            ["summary"] = summary,
            // Callers (e.g. SkillExecutor.ExtractSelectedAgentIds) also look under "content"/"output".
            ["content"] = summary
        };
        if (!string.IsNullOrEmpty(lastErrorCode) || !string.IsNullOrEmpty(lastErrorMessage))
        {
            result["errorCode"] = lastErrorCode ?? string.Empty;
            result["error"] = lastErrorMessage ?? string.Empty;
        }
        return result;
    }

    /// <summary>
    /// Extract <c>last_error.code</c> and <c>last_error.message</c> from a Foundry run element.
    /// Returns <c>(null, null)</c> when the run has no error attached (the common case while a
    /// run is still in-flight or completes successfully).
    /// </summary>
    private static (string? Code, string? Message) ExtractRunLastError(JsonElement runElement)
    {
        if (runElement.ValueKind != JsonValueKind.Object) return (null, null);
        if (!runElement.TryGetProperty("last_error", out var errEl)
            || errEl.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        var code = errEl.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;
        var message = errEl.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()
            : null;
        return (code, message);
    }

    /// <summary>
    /// Invoke a v1 hosted/connected agent by POSTing directly to its per-agent <c>agent_endpoint</c>.
    /// These agents don't share the OpenAI-Assistants /threads/runs runtime — their id is a friendly
    /// name (e.g. "Emirates-flight-agent") and they carry their own URL + protocol.
    /// </summary>
    private async Task<Dictionary<string, object>> InvokeV1HostedAgentAsync(
        string agentId, string prompt, Dictionary<string, object>? parameters, CancellationToken ct)
    {
        var agentEndpoint = await ResolveAgentEndpointAsync(agentId, ct);
        if (string.IsNullOrWhiteSpace(agentEndpoint))
        {
            _logger.LogWarning(
                "v1 hosted-agent {AgentId} has no agent_endpoint; cannot invoke.", agentId);
            return new Dictionary<string, object>
            {
                ["agentId"] = agentId,
                ["provider"] = "azure-foundry",
                ["runId"] = string.Empty,
                ["threadId"] = string.Empty,
                ["status"] = "unsupported_surface",
                ["surface"] = "agents-v1",
                ["summary"] = "v1 hosted-agent has no agent_endpoint on its definition — cannot route the request.",
                ["content"] = string.Empty
            };
        }

        // Portal/playground-created v1 agents speak the OpenAI **Responses API** on their per-agent
        // endpoint (…/agents/{id}/endpoint/protocols/openai/responses). Model + instructions are
        // baked into the agent server-side, so we only send `input`.
        var body = new Dictionary<string, object>
        {
            ["input"] = prompt,
            ["stream"] = false
        };
        if (parameters is { Count: > 0 })
            body["metadata"] = parameters;

        _logger.LogInformation(
            "Invoking v1 hosted-agent {AgentId} at {Endpoint}", agentId, agentEndpoint);

        HttpResponseMessage response;
        string content;
        try
        {
            response = await _httpClient.PostAsJsonAsync(agentEndpoint, body, ct);
            content = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "v1 hosted-agent {AgentId} POST to {Endpoint} threw.", agentId, agentEndpoint);
            return new Dictionary<string, object>
            {
                ["agentId"] = agentId,
                ["provider"] = "azure-foundry",
                ["status"] = "create_failed",
                ["surface"] = "agents-v1",
                ["summary"] = $"Network/transport error invoking hosted-agent: {ex.Message}",
                ["content"] = string.Empty
            };
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "v1 hosted-agent {AgentId} returned HTTP {Status} from {Endpoint}. Body={Body}",
                agentId, (int)response.StatusCode, agentEndpoint, Truncate(content, 500));

            return new Dictionary<string, object>
            {
                ["agentId"] = agentId,
                ["provider"] = "azure-foundry",
                ["status"] = "create_failed",
                ["surface"] = "agents-v1",
                ["summary"] = $"Hosted-agent invocation failed with HTTP {(int)response.StatusCode}.",
                ["content"] = string.Empty,
                ["error"] = Truncate(content, 500)
            };
        }

        var summary = ExtractHostedAgentText(content);
        _logger.LogInformation(
            "v1 hosted-agent {AgentId} completed. SummaryLength={Length}", agentId, summary.Length);

        return new Dictionary<string, object>
        {
            ["agentId"] = agentId,
            ["provider"] = "azure-foundry",
            ["runId"] = string.Empty,
            ["threadId"] = string.Empty,
            ["status"] = "completed",
            ["surface"] = "agents-v1",
            ["summary"] = summary,
            ["content"] = summary,
            ["rawResponse"] = Truncate(content, 2000)
        };
    }

    private Task<string> ResolveAgentEndpointAsync(string agentId, CancellationToken ct)
    {
        if (AgentEndpointCache.TryGetValue(agentId, out var cached) && !string.IsNullOrWhiteSpace(cached))
            return Task.FromResult(cached);

        // Portal/playground-created v1 agents don't advertise their invoke URL on the list
        // response — the URL is derivable by convention from the project endpoint + agent id:
        //   {projectEndpoint}/agents/{agentId}/endpoint/protocols/openai/responses
        // This is the OpenAI Responses API surface baked into the Foundry Agent Service.
        var endpoint = _options.Endpoint.TrimEnd('/');
        var conventional = $"{endpoint}/agents/{agentId}/endpoint/protocols/openai/responses?api-version=v1";
        AgentEndpointCache[agentId] = conventional;
        return Task.FromResult(conventional);
    }

    /// <summary>
    /// Look for the v1 hosted-agent invocation URL on an agent JSON element. Different Foundry
    /// releases and different agent types (persistent, connected, voice-live, …) place the
    /// invocation URL at different depths — e.g. top-level <c>agent_endpoint</c> on some,
    /// <c>versions.latest.runtime.endpoint</c> on others. We walk the entire tree looking for
    /// any property whose name matches a known endpoint-field alias.
    /// </summary>
    private static string TryExtractAgentEndpoint(JsonElement el)
    {
        // Ordered so more-specific names win over generic ones.
        var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "agent_endpoint",
            "invoke_endpoint",
            "invocation_endpoint",
            "chat_endpoint",
            "chat_completions_endpoint",
            "runtime_endpoint",
            "endpoint_url",
            "endpoint"
        };

        return FindEndpointRecursive(el, candidateNames, depth: 0) ?? string.Empty;
    }

    private static string? FindEndpointRecursive(JsonElement el, HashSet<string> names, int depth)
    {
        // Bound the walk so a pathologically deep definition can't wedge a request.
        if (depth > 8) return null;

        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                // Prefer a direct hit on any known field name at this level.
                foreach (var prop in el.EnumerateObject())
                {
                    if (names.Contains(prop.Name) && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = prop.Value.GetString();
                        if (LooksLikeUrl(s)) return s;
                    }
                }
                // Then recurse.
                foreach (var prop in el.EnumerateObject())
                {
                    var found = FindEndpointRecursive(prop.Value, names, depth + 1);
                    if (found is not null) return found;
                }
                return null;

            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    var found = FindEndpointRecursive(item, names, depth + 1);
                    if (found is not null) return found;
                }
                return null;

            default:
                return null;
        }
    }

    private static bool LooksLikeUrl(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
            || s.StartsWith("ws://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Best-effort text extraction from a hosted-agent response. Handles the OpenAI Responses
    /// API shape used by Foundry v1 hosted agents (primary), plus a few legacy shapes:
    ///   - Responses: <c>output_text</c> (convenience field), or walk <c>output[*].content[*].text</c>
    ///   - Chat completions: <c>choices[0].message.content</c>
    ///   - Simple: <c>content</c> / <c>text</c> / <c>response</c> / <c>summary</c> / <c>message</c>
    ///   - Falls back to the raw body.
    /// </summary>
    private static string ExtractHostedAgentText(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                // Responses API convenience field — the SDK-assembled final text.
                if (root.TryGetProperty("output_text", out var outputText)
                    && outputText.ValueKind == JsonValueKind.String)
                {
                    var s = outputText.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s!;
                }

                // Responses API structured output: output[*].content[*].text
                if (root.TryGetProperty("output", out var output)
                    && output.ValueKind == JsonValueKind.Array)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var item in output.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        if (!item.TryGetProperty("content", out var contentArr)
                            || contentArr.ValueKind != JsonValueKind.Array) continue;
                        foreach (var part in contentArr.EnumerateArray())
                        {
                            if (part.ValueKind != JsonValueKind.Object) continue;
                            if (part.TryGetProperty("text", out var t)
                                && t.ValueKind == JsonValueKind.String)
                            {
                                if (sb.Length > 0) sb.Append('\n');
                                sb.Append(t.GetString());
                            }
                        }
                    }
                    if (sb.Length > 0) return sb.ToString();
                }

                // Chat-completions shape (fallback for legacy responders).
                if (root.TryGetProperty("choices", out var choices)
                    && choices.ValueKind == JsonValueKind.Array
                    && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("message", out var msg)
                        && msg.TryGetProperty("content", out var mc)
                        && mc.ValueKind == JsonValueKind.String)
                    {
                        return mc.GetString() ?? string.Empty;
                    }
                }

                foreach (var name in new[] { "content", "text", "response", "summary", "message" })
                {
                    if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                        return v.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON body — just return raw.
        }

        return body;
    }

    private static bool IsTerminalRunStatus(string status) => status switch
    {
        // "requires_action" (tool calls) and "incomplete" are semi-terminal — we don't auto-continue
        // tool-calling here, so we treat them as terminal for the caller to handle.
        "completed" or "failed" or "cancelled" or "expired" or "requires_action" or "incomplete" => true,
        _ => false
    };

    private static string ExtractFirstAssistantText(string messagesJson)
    {
        using var doc = JsonDocument.Parse(messagesJson);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return string.Empty;

        // With order=desc the newest message is first; the assistant reply we want is the most
        // recent assistant-role message.
        foreach (var msg in data.EnumerateArray())
        {
            var role = msg.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() : null;
            if (role != "assistant") continue;

            if (!msg.TryGetProperty("content", out var contentArr) || contentArr.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in contentArr.EnumerateArray())
            {
                var type = part.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() : null;
                if (type == "text"
                    && part.TryGetProperty("text", out var textObj)
                    && textObj.ValueKind == JsonValueKind.Object
                    && textObj.TryGetProperty("value", out var textVal)
                    && textVal.ValueKind == JsonValueKind.String)
                {
                    return textVal.GetString() ?? string.Empty;
                }
            }
        }
        return string.Empty;
    }

    private static List<string> ExtractCapabilities(JsonElement element)
    {
        var capabilities = new List<string>();
        if (element.TryGetProperty("tools", out var tools))
        {
            foreach (var tool in tools.EnumerateArray())
            {
                if (tool.TryGetProperty("type", out var toolType))
                    capabilities.Add(toolType.GetString() ?? string.Empty);
            }
        }
        return capabilities;
    }

    /// <summary>
    /// Pulls a rich, JSON-serializable snapshot of an agent definition (model, instructions,
    /// tags, capabilities, MCP endpoints, authority, tools, metadata, created_at) into a flat
    /// dictionary that the Agent Mesh Platform UI renders in its "agent config" popup.
    /// Best-effort — every property is optional and every missing field is silently skipped.
    /// </summary>
    private static Dictionary<string, object> ExtractConfiguration(JsonElement element, string surface)
    {
        var config = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["surface"] = surface
        };

        if (element.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
            config["model"] = modelEl.GetString() ?? string.Empty;

        if (element.TryGetProperty("instructions", out var instrEl) && instrEl.ValueKind == JsonValueKind.String)
        {
            var instr = instrEl.GetString() ?? string.Empty;
            config["instructions"] = instr.Length > 4000 ? instr[..4000] + "…" : instr;
        }

        if (element.TryGetProperty("created_at", out var createdEl)
            && (createdEl.ValueKind == JsonValueKind.Number || createdEl.ValueKind == JsonValueKind.String))
        {
            config["created_at"] = createdEl.ToString();
        }

        // metadata is where OnboardAgentRequest stashes tags, capabilities, mcp_server_url*, authority, provider
        if (element.TryGetProperty("metadata", out var metaEl) && metaEl.ValueKind == JsonValueKind.Object)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in metaEl.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    metadata[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
            config["metadata"] = metadata;

            if (metadata.TryGetValue("tags", out var tagsCsv) && !string.IsNullOrWhiteSpace(tagsCsv))
                config["tags"] = tagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (metadata.TryGetValue("capabilities", out var capsCsv) && !string.IsNullOrWhiteSpace(capsCsv))
                config["capabilities"] = capsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (metadata.TryGetValue("mcp_server_url", out var mcp) && !string.IsNullOrWhiteSpace(mcp))
                config["mcp_server_url"] = mcp;
            if (metadata.TryGetValue("mcp_server_url_discovery", out var mcpD) && !string.IsNullOrWhiteSpace(mcpD))
                config["mcp_server_url_discovery"] = mcpD;
            if (metadata.TryGetValue("mcp_server_url_execution", out var mcpE) && !string.IsNullOrWhiteSpace(mcpE))
                config["mcp_server_url_execution"] = mcpE;
            if (metadata.TryGetValue("mcp_server_url_confirmation", out var mcpC) && !string.IsNullOrWhiteSpace(mcpC))
                config["mcp_server_url_confirmation"] = mcpC;
            if (metadata.TryGetValue("authority", out var auth) && !string.IsNullOrWhiteSpace(auth))
                config["authority"] = auth;
            if (metadata.TryGetValue("provider", out var provider) && !string.IsNullOrWhiteSpace(provider))
                config["provider"] = provider;
        }

        // Summarize tools (type + server_label + server_url) — omit auth headers etc.
        if (element.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
        {
            var toolSummaries = new List<Dictionary<string, string>>();
            foreach (var tool in toolsEl.EnumerateArray())
            {
                if (tool.ValueKind != JsonValueKind.Object) continue;
                var summary = new Dictionary<string, string>(StringComparer.Ordinal);
                if (tool.TryGetProperty("type", out var tType) && tType.ValueKind == JsonValueKind.String)
                    summary["type"] = tType.GetString() ?? string.Empty;
                if (tool.TryGetProperty("server_label", out var tLabel) && tLabel.ValueKind == JsonValueKind.String)
                    summary["server_label"] = tLabel.GetString() ?? string.Empty;
                if (tool.TryGetProperty("server_url", out var tUrl) && tUrl.ValueKind == JsonValueKind.String)
                    summary["server_url"] = tUrl.GetString() ?? string.Empty;
                if (summary.Count > 0)
                    toolSummaries.Add(summary);
            }
            if (toolSummaries.Count > 0)
                config["tools"] = toolSummaries;
        }

        return config;
    }
}
