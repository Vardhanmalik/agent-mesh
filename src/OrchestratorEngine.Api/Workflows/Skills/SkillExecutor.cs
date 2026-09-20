using Microsoft.Extensions.Options;
using OrchestratorEngine.Api.Configuration;
using OrchestratorEngine.Api.Models;
using OrchestratorEngine.Api.Services;
using System.Text.Json;

namespace OrchestratorEngine.Api.Workflows.Skills;

public sealed class SkillExecutor : ISkillExecutor
{
    private readonly IFoundryAgentService _foundryAgentService;
    private readonly IUserContextService _userContextService;
    private readonly IVectorStorageService _vectorStorage;
    private readonly ISessionStore _sessionStore;
    private readonly FoundryOptions _foundryOptions;
    private readonly ILogger<SkillExecutor> _logger;

    public SkillExecutor(
        IFoundryAgentService foundryAgentService,
        IUserContextService userContextService,
        IVectorStorageService vectorStorage,
        ISessionStore sessionStore,
        IOptions<FoundryOptions> foundryOptions,
        ILogger<SkillExecutor> logger)
    {
        _foundryAgentService = foundryAgentService;
        _userContextService = userContextService;
        _vectorStorage = vectorStorage;
        _sessionStore = sessionStore;
        _foundryOptions = foundryOptions.Value;
        _logger = logger;
    }

    public async Task<T?> ExecuteAsync<T>(string skillName, Dictionary<string, object> parameters, CancellationToken ct = default)
    {
        _logger.LogInformation("Executing skill: {SkillName}", skillName);

        object? result = skillName switch
        {
            SkillNames.FetchAvailableAgents => await ExecuteFetchAgentsAsync(parameters, ct),
            SkillNames.SelectBestAgent => await ExecuteSelectBestAgentAsync(parameters, ct),
            SkillNames.RankOptions => await ExecuteRankOptionsAsync(parameters, ct),
            SkillNames.ExecuteAgentAction => await ExecuteAgentActionAsync(parameters, ct),
            SkillNames.ConfirmBooking => await ExecuteConfirmBookingAsync(parameters, ct),
            SkillNames.StoreContextEmbedding => await ExecuteStoreEmbeddingAsync(parameters, ct),
            _ => throw new InvalidOperationException($"Unknown skill: {skillName}")
        };

        if (result is T typed) return typed;

        // Attempt JSON round-trip conversion for complex types
        var json = JsonSerializer.Serialize(result);
        return JsonSerializer.Deserialize<T>(json);
    }

    private async Task<List<AgentInfo>> ExecuteFetchAgentsAsync(
        Dictionary<string, object> parameters, CancellationToken ct)
    {
        var prompt = parameters["prompt"]?.ToString() ?? string.Empty;
        var allAgents = await _foundryAgentService.ListAvailableAgentsAsync(ct);

        // Classify the prompt into a coarse domain. If we can classify it, keep only agents
        // whose own metadata classifies into the SAME domain. This is what prevents a pizza
        // prompt from fanning out to flight agents and vice versa.
        var (promptDomain, promptScore) = ClassifyDomain(prompt);
        if (string.IsNullOrEmpty(promptDomain))
        {
            // When the prompt can't be classified into any known domain, DO NOT fan out to
            // every agent — that's how a Lenovo (electronics) agent ended up answering a
            // vague food prompt with hallucinated generic options. Instead, keep only agents
            // that are themselves domain-neutral (no classified domain). If none, return
            // empty and let the workflow surface a clarification.
            var neutral = allAgents
                .Where(a => string.IsNullOrEmpty(ClassifyAgent(a).Domain))
                .ToList();
            _logger.LogInformation(
                "FetchAgents: prompt did not classify into a known domain; keeping only domain-neutral agents. Kept {Kept}/{Total}. Kept=[{Names}]",
                neutral.Count, allAgents.Count,
                string.Join(", ", neutral.Select(a => $"'{a.Name}'")));
            return neutral;
        }

        var matching = new List<AgentInfo>();
        foreach (var a in allAgents)
        {
            var (agentDomain, agentScore) = ClassifyAgent(a);
            // Strict: an agent must classify to the SAME domain as the prompt.
            // Previously an agent with no classified domain (agentDomain == "") was silently
            // dropped, which is the desired behavior here too — a truly generic agent should
            // not answer a food prompt with fabricated food options.
            var keep = !string.IsNullOrEmpty(agentDomain)
                && string.Equals(agentDomain, promptDomain, StringComparison.OrdinalIgnoreCase);
            _logger.LogInformation(
                "FetchAgents classify: id={AgentId} name='{Name}' surface={Surface} → domain='{AgentDomain}' (score={AgentScore}) — {Decision}",
                a.AgentId, a.Name, a.Surface, agentDomain, agentScore,
                keep ? "KEEP" : $"DROP (want '{promptDomain}')");
            if (keep) matching.Add(a);
        }

        _logger.LogInformation(
            "FetchAgents: prompt classified as '{Domain}' (score={Score}); {Kept}/{Total} agents match that domain. Kept=[{Names}]",
            promptDomain, promptScore, matching.Count, allAgents.Count,
            string.Join(", ", matching.Select(a => $"'{a.Name}'")));

        // If nothing matches, prefer returning empty (so the workflow reports "no suitable agent")
        // over silently fanning out to wrong-domain agents.
        return matching;
    }

    private async Task<List<AgentInfo>> ExecuteSelectBestAgentAsync(
        Dictionary<string, object> parameters, CancellationToken ct)
    {
        var prompt = parameters["prompt"]?.ToString() ?? string.Empty;
        var agents = DeserializeParam<List<AgentInfo>>(parameters["agents"]);
        var userContext = DeserializeParam<UserContext>(parameters["userContext"]);

        if (agents is null || agents.Count == 0) return [];

        // Score agents based on prompt relevance and user preferences
        var scored = new List<(AgentInfo Agent, double Score)>();

        foreach (var agent in agents)
        {
            var score = CalculateAgentScore(agent, prompt, userContext);
            scored.Add((agent, score));
        }

        // Check user's historical preferences from vector store
        if (userContext != null)
        {
            var history = await _userContextService.GetRelevantContextAsync(
                userContext.UserId, prompt, ct);

            foreach (var (agent, _) in scored.ToList())
            {
                var historyBonus = history
                    .Count(h => h.Content.Contains(agent.Name, StringComparison.OrdinalIgnoreCase)) * 0.1;
                var idx = scored.FindIndex(s => s.Agent.AgentId == agent.AgentId);
                if (idx >= 0)
                    scored[idx] = (agent, scored[idx].Score + historyBonus);
            }
        }

        var ranked = scored
            .OrderByDescending(s => s.Score)
            // Deterministic tie-break so a set of same-domain agents that all score identically
            // (e.g. Emirates + AirIndia + Indigo for a plain "book a flight" prompt) don't get
            // re-shuffled between calls based on the Foundry list order. Name is stable across
            // the /assistants + /agents-v1 dedupe and matches what the user sees on cards.
            .ThenBy(s => s.Agent.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Agent.AgentId, StringComparer.Ordinal)
            .ToList();

        _logger.LogInformation(
            "SelectBestAgent ranked ({Count}): [{Ranked}]",
            ranked.Count,
            string.Join(", ", ranked.Select(r => $"'{r.Agent.Name}'={r.Score:F2}")));

        // Hand the heuristically-ranked candidates to a Foundry LLM for the final call. If no selector
        // agent is configured or the call fails, fall back to the heuristic ranking alone.
        var llmSelection = await TrySelectWithLlmAsync(prompt, ranked, userContext, ct);
        if (llmSelection is { Count: > 0 })
        {
            _logger.LogInformation(
                "SelectBestAgent: LLM selector picked {Count} agent(s): [{Names}]",
                llmSelection.Count,
                string.Join(", ", llmSelection.Select(a => $"'{a.Name}'")));
            return llmSelection;
        }

        // Drop agents with score <= 0 so we don't fan out to obviously-wrong agents (e.g. a
        // pizza prompt scoring 0 against Emirates-flight-agent). Cap fan-out to 5 to keep
        // Foundry rate limits happy; the WorkflowEngine's SemaphoreSlim further throttles.
        var positive = ranked.Where(s => s.Score > 0).ToList();
        if (positive.Count == 0)
        {
            // No positive matches. Return the pre-filtered set unchanged so the caller can still
            // fan out — the domain filter in ExecuteFetchAgentsAsync already narrowed the pool.
            // Return up to 3 (not 2) to match the generic-query "top 3 agents × 3 options" shape
            // the WorkflowEngine expects downstream.
            var fallback = ranked.Take(3).Select(s => s.Agent).ToList();
            _logger.LogInformation(
                "SelectBestAgent: no positively-scored candidates; falling back to top-3 by tie-break: [{Names}]",
                string.Join(", ", fallback.Select(a => $"'{a.Name}'")));
            return fallback;
        }

        var chosen = positive
            .Take(5)
            .Select(s => s.Agent)
            .ToList();
        _logger.LogInformation(
            "SelectBestAgent: returning {Count} positively-scored agent(s): [{Names}]",
            chosen.Count,
            string.Join(", ", chosen.Select(a => $"'{a.Name}'")));
        return chosen;
    }

    private async Task<List<AgentInfo>?> TrySelectWithLlmAsync(
        string prompt, List<(AgentInfo Agent, double Score)> ranked, UserContext? userContext, CancellationToken ct)
    {
        var selectorAgentId = _foundryOptions.SelectorAgentId;
        if (!IsUsableSelectorAgentId(selectorAgentId))
            return null;

        var candidates = ranked.Take(5).Select(s => new
        {
            agentId = s.Agent.AgentId,
            name = s.Agent.Name,
            description = s.Agent.Description,
            capabilities = s.Agent.Capabilities,
            heuristicScore = Math.Round(s.Score, 2)
        });

        var rankingPrompt = JsonSerializer.Serialize(new
        {
            instruction = "Given the user's request, their preferences, and a heuristically pre-scored list of " +
                "candidate agents, choose the 3 best agents to fulfil the request (fewer than 3 only if the " +
                "candidate list is smaller). Prefer distinct providers over duplicates from the same brand. " +
                "Respond ONLY with JSON in the form {\"selectedAgentIds\": [\"<agentId>\", ...]}.",
            userPrompt = prompt,
            userPreferences = userContext?.Preferences ?? [],
            candidates
        });

        try
        {
            var response = await _foundryAgentService.InvokeAgentAsync(selectorAgentId, rankingPrompt, ct: ct);
            var selectedIds = ExtractSelectedAgentIds(response);
            if (selectedIds.Count == 0)
            {
                _logger.LogWarning("Selector agent {AgentId} returned no usable selection; falling back to heuristic ranking.", selectorAgentId);
                return null;
            }

            var byId = ranked.ToDictionary(s => s.Agent.AgentId, s => s.Agent);
            var selected = selectedIds
                .Where(byId.ContainsKey)
                .Select(id => byId[id])
                .Take(3)
                .ToList();

            return selected.Count > 0 ? selected : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM-assisted agent selection via {AgentId} failed; falling back to heuristic ranking.", selectorAgentId);
            return null;
        }
    }

    private static List<string> ExtractSelectedAgentIds(Dictionary<string, object> response)
    {
        // Preferred shape: the selector agent responds with { "selectedAgentIds": [...] } directly.
        if (response.TryGetValue("selectedAgentIds", out var idsValue))
            return ParseIdsValue(idsValue);

        // Fallback shapes: the ids may be embedded as a JSON string inside a text field.
        foreach (var key in new[] { "summary", "content", "output" })
        {
            if (!response.TryGetValue(key, out var textValue) || textValue is null)
                continue;

            var text = textValue.ToString();
            if (string.IsNullOrWhiteSpace(text))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("selectedAgentIds", out var arr))
                    return ParseIdsValue(arr);
            }
            catch (JsonException)
            {
                // Not JSON; try the next candidate field.
            }
        }

        return [];
    }

    private static List<string> ParseIdsValue(object idsValue)
    {
        switch (idsValue)
        {
            case JsonElement { ValueKind: JsonValueKind.Array } element:
                return element.EnumerateArray()
                    .Select(e => e.GetString() ?? string.Empty)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            case IEnumerable<object> list:
                return list.Select(o => o?.ToString() ?? string.Empty)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
            default:
                return [];
        }
    }

    /// <summary>
    /// Aggregates and normalizes raw agent responses into a small set of ranked, structured options
    /// suitable for surfacing to the user. Input parameters:
    ///   <c>prompt</c>              — the original user request
    ///   <c>userContext</c>         — optional UserContext
    ///   <c>rawResults</c>          — <see cref="List{T}"/> of Dictionary&lt;string,object&gt; from InvokeAgentAsync
    ///   <c>maxOptions</c>          — optional int (default 3), the total upper bound
    ///   <c>maxOptionsPerAgent</c>  — optional int (default 0 = no per-agent quota). When &gt; 0,
    ///                                enforces a per-agent quota so a single loud agent can't
    ///                                monopolize the result set. Used for generic-query fan-out
    ///                                where we want "top N per agent × top M agents".
    ///   <c>maxAgents</c>           — optional int (default 0 = no cap). When &gt; 0, the ranker
    ///                                keeps only the top-M agent groups (by best in-group score).
    /// Returns a <see cref="List{T}"/> of <see cref="AgentOption"/> — never null, may be empty.
    /// </summary>
    private async Task<List<AgentOption>> ExecuteRankOptionsAsync(
        Dictionary<string, object> parameters, CancellationToken ct)
    {
        var prompt = parameters.TryGetValue("prompt", out var p) ? p?.ToString() ?? string.Empty : string.Empty;
        var userContext = parameters.TryGetValue("userContext", out var uc) ? DeserializeParam<UserContext>(uc) : null;
        var maxOptions = parameters.TryGetValue("maxOptions", out var mo) && int.TryParse(mo?.ToString(), out var mi) ? mi : 3;
        var maxOptionsPerAgent = parameters.TryGetValue("maxOptionsPerAgent", out var mop)
            && int.TryParse(mop?.ToString(), out var mpi) ? mpi : 0;
        var maxAgents = parameters.TryGetValue("maxAgents", out var ma)
            && int.TryParse(ma?.ToString(), out var mai) ? mai : 0;
        var promptDomain = parameters.TryGetValue("promptDomain", out var pd) ? pd?.ToString() ?? string.Empty : string.Empty;
        var rawResults = DeserializeParam<List<Dictionary<string, object>>>(parameters["rawResults"]) ?? [];
        var agentType = DomainToAgentType(promptDomain);

        // Step 1: explode each agent response into 1..N candidate options. If an agent returned
        // a JSON array of offers we treat each element as its own option; otherwise the whole
        // summary is one option.
        var candidates = new List<AgentOption>();
        foreach (var raw in rawResults)
        {
            var agentId = raw.TryGetValue("agentId", out var aid) ? aid?.ToString() ?? string.Empty : string.Empty;
            var summary = raw.TryGetValue("summary", out var s) ? s?.ToString() ?? string.Empty : string.Empty;
            var agentName = raw.TryGetValue("agentName", out var an) ? an?.ToString() ?? agentId : agentId;
            var status = raw.TryGetValue("status", out var st) ? st?.ToString() ?? string.Empty : string.Empty;

            // Skip failed/unsupported runs — they carry no useful offer data.
            if (status is "create_failed" or "failed" or "unsupported_surface")
            {
                _logger.LogInformation(
                    "RankOptions: skipping agent {AgentId} because status={Status}", agentId, status);
                continue;
            }

            var exploded = ExplodeAgentResponseToOptions(summary, agentId, agentName, agentType, raw);

            // Per-agent explode summary — critical for diagnosing "why did only one agent
            // show up?" cases. Without this, an agent that returned an empty JSON array
            // (correctly signalling "no matches") looks identical to one that was never
            // invoked. Log the count and the first-title preview so we can trace back to
            // the agent that ate a card silently.
            _logger.LogInformation(
                "RankOptions: agent {AgentId} ({AgentName}) → {Count} candidate option(s). Titles=[{Titles}]",
                agentId, agentName, exploded.Count,
                string.Join(", ", exploded.Take(3).Select(o => $"'{o.Title ?? "(no-title)"}'")));

            // Defensively drop options that look like clarification questions rather than concrete
            // offers. The BuildEnrichedPrompt explicitly forbids clarifications, but occasionally an
            // agent still asks — those responses have no structured price/title AND contain
            // clarification signals (question marks + confirm-style verbs).
            foreach (var opt in exploded)
            {
                if (IsClarificationOnly(opt))
                {
                    _logger.LogInformation(
                        "RankOptions: dropping clarification-only response from agent {AgentId}.", agentId);
                    continue;
                }
                candidates.Add(opt);
            }
        }

        _logger.LogInformation(
            "RankOptions: exploded {AgentCount} agent response(s) into {CandidateCount} candidate option(s) " +
            "(maxOptions={Max}, perAgent={PerAgent}, maxAgents={MaxAgents}).",
            rawResults.Count, candidates.Count, maxOptions, maxOptionsPerAgent, maxAgents);

        if (candidates.Count == 0)
            return [];

        // Step 2: LLM-assisted ranking (only when a selector agent is configured — off by default).
        // The LLM ranker collapses everything into a single top-N; skip it when a per-agent quota
        // is requested so we don't lose the "3 per agent × 3 agents" shape the caller asked for.
        if (maxOptionsPerAgent <= 0 && IsUsableSelectorAgentId(_foundryOptions.SelectorAgentId))
        {
            var llmRanked = await TryRankWithLlmAsync(prompt, userContext, candidates, maxOptions, ct);
            if (llmRanked is { Count: > 0 })
                return llmRanked;
        }

        // Step 3: Heuristic ranking against user preferences + vector-storage history.
        return await HeuristicRankAsync(candidates, prompt, userContext, maxOptions, maxOptionsPerAgent, maxAgents, ct);
    }

    /// <summary>
    /// Score every candidate against the user's stated preferences and their vector-storage history.
    /// When <paramref name="maxOptionsPerAgent"/> is 0, returns the flat top <paramref name="maxOptions"/>
    /// across all agents (legacy behavior). When it's &gt; 0, groups candidates by agent, keeps the top
    /// <paramref name="maxOptionsPerAgent"/> in each group, then keeps the top <paramref name="maxAgents"/>
    /// groups (ranked by the best-scoring option in each), preserving per-agent ordering.
    /// </summary>
    private async Task<List<AgentOption>> HeuristicRankAsync(
        List<AgentOption> candidates, string prompt, UserContext? userContext,
        int maxOptions, int maxOptionsPerAgent, int maxAgents, CancellationToken ct)
    {
        // Pull relevant historical context once — reused across all candidates.
        List<UserEmbeddingDocument> history = [];
        if (userContext is not null && !string.IsNullOrWhiteSpace(userContext.UserId))
        {
            try
            {
                history = await _userContextService.GetRelevantContextAsync(userContext.UserId, prompt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "HeuristicRankAsync: vector context lookup failed; continuing without history.");
            }
        }

        var preferenceTokens = userContext?.Preferences
            .SelectMany(kv => Tokenize(kv.Value?.ToString() ?? string.Empty))
            .Where(t => t.Length > 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var historyTokens = history
            .SelectMany(h => Tokenize(h.Content))
            .Where(t => t.Length > 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var scored = candidates.Select(c => new { Option = c, Score = ScoreOption(c, preferenceTokens, historyTokens) })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Option.PriceValue.HasValue) // prefer options with a concrete price
            .ToList();

        // Per-agent quota mode: enforce fair fan-out across agents.
        if (maxOptionsPerAgent > 0)
        {
            var perAgentTop = scored
                .GroupBy(x => x.Option.AgentId, StringComparer.Ordinal)
                .Select(g => new
                {
                    AgentId = g.Key,
                    Top = g.Take(maxOptionsPerAgent).ToList(),
                    BestScore = g.Max(x => x.Score)
                })
                .OrderByDescending(g => g.BestScore)
                .ToList();

            if (maxAgents > 0 && perAgentTop.Count > maxAgents)
                perAgentTop = perAgentTop.Take(maxAgents).ToList();

            var flattened = perAgentTop
                .SelectMany(g => g.Top)
                .Take(maxOptions > 0 ? maxOptions : int.MaxValue)
                .ToList();

            foreach (var s in flattened)
            {
                _logger.LogDebug(
                    "RankOptions: [per-agent] {AgentName} option {OptionId} scored {Score:F2} (title={Title})",
                    s.Option.AgentName, s.Option.OptionId, s.Score, s.Option.Title ?? "(none)");
            }

            return flattened.Select(x => x.Option).ToList();
        }

        // Flat top-N mode (legacy).
        foreach (var s in scored.Take(maxOptions))
        {
            _logger.LogDebug(
                "RankOptions: {AgentName} option {OptionId} scored {Score:F2} (title={Title})",
                s.Option.AgentName, s.Option.OptionId, s.Score, s.Option.Title ?? "(none)");
        }

        return scored.Take(maxOptions).Select(x => x.Option).ToList();
    }

    private static double ScoreOption(AgentOption o, HashSet<string> prefTokens, HashSet<string> histTokens)
    {
        var text = string.Join(" ", new[] { o.Title, o.Description, o.Details }.Where(s => !string.IsNullOrEmpty(s))!)
            + " " + string.Join(" ", o.Attributes.Values);
        var tokens = Tokenize(text).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var prefMatches = prefTokens.Count == 0 ? 0 : prefTokens.Count(tokens.Contains);
        var histMatches = histTokens.Count == 0 ? 0 : histTokens.Count(tokens.Contains);

        // Preference matches weighted higher than incidental history overlap.
        var score = (prefMatches * 2.0) + (histMatches * 0.25);
        if (o.PriceValue.HasValue) score += 0.5; // concrete offers slightly favored
        if (!string.IsNullOrWhiteSpace(o.Title)) score += 0.25;
        return score;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string DomainToAgentType(string domain) => domain switch
    {
        "flights" or "hotels" => "Travel",
        "food-delivery" => "Food",
        "dining" => "Dining",
        "" or "general" => "General",
        _ => char.ToUpperInvariant(domain[0]) + domain[1..]
    };

    /// <summary>
    /// Detect an agent response that is only a clarification question rather than a concrete
    /// offer. Signature: no structured Title AND no Price AND text contains a question mark
    /// AND at least one clarification-verb token. Kept conservative so genuine offers with
    /// trailing "Would you like me to book this?" aren't dropped.
    /// </summary>
    private static bool IsClarificationOnly(AgentOption opt)
    {
        // If the agent gave us any structured signal, treat it as a real offer.
        if (opt.PriceValue.HasValue) return false;
        if (!string.IsNullOrWhiteSpace(opt.Title)) return false;
        if (opt.Attributes.Count > 0) return false;

        var text = (opt.Description ?? opt.Details ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text)) return true; // empty response is not useful
        if (!text.Contains('?')) return false;

        string[] clarificationSignals =
        {
            "clarification", "clarify", "confirm", "please confirm", "could you confirm",
            "which date", "which day", "which cabin", "which class", "how many",
            "one-way or return", "one way or return", "one-way or round", "round trip or",
            "may i", "shall i", "should i", "before i search", "before i proceed",
            "before i present", "let me know"
        };
        foreach (var s in clarificationSignals)
        {
            if (text.Contains(s, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Guards against the appsettings.json placeholder (<c>&lt;your-agent-selector-agent-id&gt;</c>)
    /// being sent as a real id. An unset or placeholder value means "no LLM ranker configured".
    /// </summary>
    private static bool IsUsableSelectorAgentId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (id.Contains('<') || id.Contains('>')) return false;
        return true;
    }

    private List<AgentOption> ExplodeAgentResponseToOptions(
        string summary, string agentId, string agentName, string agentType, Dictionary<string, object> raw)
    {
        var results = new List<AgentOption>();
        if (string.IsNullOrWhiteSpace(summary))
        {
            _logger.LogInformation(
                "RankOptions: agent {AgentId} ({AgentName}) returned an empty summary; treating as no-offer.",
                agentId, agentName);
            return results;
        }

        // Try to find a JSON array/object of offers inside the summary. Agents wrap JSON in
        // ```json fences or preface it with markdown text (which itself contains stray brackets
        // like `[emirates.com](https://…)`), so we scan for every candidate span and keep the
        // first one that actually parses.
        //
        // IMPORTANT: an agent that returns an empty JSON array (or a wrapper containing an
        // empty array) is EXPLICITLY telling us "I have no matches for this request". Rule 4
        // in BuildEnrichedPrompt instructs agents to do exactly that. We must NOT fall through
        // to BuildFallbackOption in that case — doing so used to surface an empty pick card
        // whose title was literally "```json" and description was "[]", which is what the user
        // reported as an "empty option saying json".
        var sawParsedJson = false;
        foreach (var jsonSpan in ExtractJsonSpans(summary))
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonSpan);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    sawParsedJson = true;
                    if (root.GetArrayLength() == 0)
                    {
                        _logger.LogInformation(
                            "RankOptions: agent {AgentId} ({AgentName}) returned an empty JSON array — no matches for this prompt.",
                            agentId, agentName);
                        return results; // explicit "nothing from me"; do NOT fabricate a fallback card.
                    }
                    foreach (var el in root.EnumerateArray())
                        results.Add(BuildOptionFromJsonElement(el, agentId, agentName, agentType, raw, fallbackSummary: summary));
                    break;
                }
                if (root.ValueKind == JsonValueKind.Object)
                {
                    sawParsedJson = true;
                    // Also accept { "options": [ … ] } / { "results": [ … ] } wrappers, INCLUDING
                    // the empty-array case (same "explicit no-match" semantics as a bare `[]`).
                    var wrapperName = new[] { "options", "results", "offers", "flights" }
                        .FirstOrDefault(n => root.TryGetProperty(n, out var w) && w.ValueKind == JsonValueKind.Array);
                    if (wrapperName is not null)
                    {
                        var inner = root.GetProperty(wrapperName);
                        if (inner.GetArrayLength() == 0)
                        {
                            _logger.LogInformation(
                                "RankOptions: agent {AgentId} ({AgentName}) returned {{\"{Wrapper}\": []}} — no matches for this prompt.",
                                agentId, agentName, wrapperName);
                            return results;
                        }
                        foreach (var el in inner.EnumerateArray())
                            results.Add(BuildOptionFromJsonElement(el, agentId, agentName, agentType, raw, fallbackSummary: summary));
                        break;
                    }

                    // A bare object with no offers-wrapper: only accept it as an offer if it
                    // actually carries offer-shaped fields (title / description / price). Anything
                    // else (e.g. `{}` or `{"status":"ok"}`) is a null response — don't fabricate.
                    var looksLikeOffer =
                        root.TryGetProperty("title", out _) ||
                        root.TryGetProperty("name", out _) ||
                        root.TryGetProperty("description", out _) ||
                        root.TryGetProperty("price", out _) ||
                        root.TryGetProperty("fare", out _);
                    if (!looksLikeOffer)
                    {
                        _logger.LogInformation(
                            "RankOptions: agent {AgentId} ({AgentName}) returned a JSON object with no offer-shaped fields — skipping.",
                            agentId, agentName);
                        return results;
                    }
                    results.Add(BuildOptionFromJsonElement(root, agentId, agentName, agentType, raw, fallbackSummary: summary));
                    break;
                }
            }
            catch (JsonException)
            {
                // Not real JSON (e.g. a markdown link `[label](url)`) — try the next span.
            }
        }

        // If the agent returned free-form prose (no JSON at all), keep the legacy fallback so we
        // don't silently drop a real offer described in natural language. But if the agent DID
        // emit parseable JSON and we still got here, that means every JSON structure it produced
        // was empty/malformed — treat it as no-match rather than showing a "```json" card.
        if (results.Count == 0 && !sawParsedJson)
            results.Add(BuildFallbackOption(summary, agentId, agentName, agentType, raw));

        return results;
    }

    private static AgentOption BuildOptionFromJsonElement(
        JsonElement el, string agentId, string agentName, string agentType, Dictionary<string, object> raw, string fallbackSummary)
    {
        static string? Str(JsonElement e, params string[] names)
        {
            foreach (var n in names)
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v))
                {
                    if (v.ValueKind == JsonValueKind.String) return v.GetString();
                    if (v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return v.ToString();
                }
            }
            return null;
        }

        static decimal? Dec(JsonElement e, params string[] names)
        {
            foreach (var n in names)
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v))
                {
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
                    if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var dp)) return dp;
                }
            }
            return null;
        }

        var title = Str(el, "title", "name", "flight", "airline", "option");
        var description = Str(el, "description", "summary");
        var currency = Str(el, "currency", "priceCurrency");
        var priceValue = Dec(el, "price", "fare", "total", "amount");

        // Collect everything else as attributes — but skip the fields we already captured to
        // avoid duplicating them in the flat details string.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "title", "name", "option", "description", "summary", "details",
            "price", "fare", "total", "amount", "currency", "priceCurrency"
        };
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
            {
                if (reserved.Contains(prop.Name)) continue;
                if (prop.Value.ValueKind is JsonValueKind.String)
                    attributes[prop.Name] = prop.Value.GetString() ?? string.Empty;
                else if (prop.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    attributes[prop.Name] = prop.Value.ToString();
            }
        }

        var detailsText = ComposeDetails(title, description, attributes, fallbackSummary);
        var priceText = FormatPrice(priceValue, currency);

        return new AgentOption
        {
            OptionId = Guid.NewGuid().ToString("N"),
            AgentId = agentId,
            AgentName = agentName,
            AgentType = agentType,
            Details = detailsText,
            Price = priceText,
            Title = title,
            Description = description ?? fallbackSummary,
            PriceValue = priceValue,
            Currency = currency,
            Attributes = attributes,
            Summary = title ?? description ?? fallbackSummary,
            Raw = raw
        };
    }

    private static AgentOption BuildFallbackOption(
        string summary, string agentId, string agentName, string agentType, Dictionary<string, object> raw)
    {
        var text = string.IsNullOrWhiteSpace(summary) ? "Option available" : summary.Trim();
        return new AgentOption
        {
            OptionId = Guid.NewGuid().ToString("N"),
            AgentId = agentId,
            AgentName = agentName,
            AgentType = agentType,
            Details = text,
            Price = null,
            Summary = text,
            Description = text,
            Raw = raw
        };
    }

    /// <summary>
    /// Compose a short, prompt-relevant details string. Keeps only the most decision-worthy
    /// signal (description OR title) plus at most two curated primary attributes. Full
    /// attribute maps and price still travel on <see cref="AgentOption"/> for the client to
    /// render however it wants — this is only the flat <c>details</c> field on the wire.
    /// </summary>
    private static string ComposeDetails(string? title, string? description, Dictionary<string, string> attributes, string fallbackSummary)
    {
        // Prefer description (usually the concrete offer sentence). Fall back to title, then
        // to the agent's fallback summary. Trim aggressively — details is intentionally short.
        var main = FirstNonBlank(description, title, fallbackSummary)?.Trim() ?? string.Empty;
        main = CollapseWhitespace(main);
        main = TrimSentence(main, maxChars: 180);

        // Append up to two "primary" attributes — the ones that materially help a user pick.
        var extras = new List<string>();
        foreach (var key in PrimaryDetailAttributeKeys)
        {
            if (extras.Count == 2) break;
            if (attributes.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val))
                extras.Add($"{PrettyKey(key)}: {val.Trim()}");
        }

        if (extras.Count > 0)
            main = string.IsNullOrEmpty(main) ? string.Join(" · ", extras) : $"{main} · {string.Join(" · ", extras)}";

        return string.IsNullOrEmpty(main) ? fallbackSummary.Trim() : main;
    }

    /// <summary>Attribute keys we surface in the compact <c>details</c> string, in priority order.</summary>
    private static readonly string[] PrimaryDetailAttributeKeys =
    {
        "departure", "arrival", "duration", "stops", "cabin", "airline",
        "eta", "delivery_eta", "cuisine", "rating", "distance", "size", "check_in", "check_out"
    };

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }

    private static string CollapseWhitespace(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        var prevSpace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }
        return sb.ToString().Trim();
    }

    private static string TrimSentence(string s, int maxChars)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxChars) return s;
        var cut = s[..maxChars];
        var lastPeriod = cut.LastIndexOfAny(new[] { '.', '!', '?' });
        // Only honor a sentence break if it lands late enough to be useful (>= 60% of budget).
        if (lastPeriod >= maxChars * 6 / 10) return cut[..(lastPeriod + 1)];
        return cut.TrimEnd() + "…";
    }

    private static string PrettyKey(string key)
    {
        // "departure_time" → "Departure time"
        var spaced = key.Replace('_', ' ').Replace('-', ' ');
        if (spaced.Length == 0) return key;
        return char.ToUpperInvariant(spaced[0]) + spaced[1..];
    }

    private static string? FormatPrice(decimal? value, string? currency)
    {
        if (!value.HasValue) return null;
        var cur = (currency ?? "USD").Trim().ToUpperInvariant();
        var symbol = cur switch { "USD" => "$", "EUR" => "€", "GBP" => "£", "INR" => "₹", "JPY" => "¥", _ => "" };
        // Prefer the currency-symbol prefix form (matches FoundryResponse.json's "700$" style is
        // a symbol suffix — support both by placing the symbol AFTER the number when known,
        // otherwise use the ISO code).
        return symbol.Length > 0
            ? $"{value.Value}{symbol}"
            : $"{value.Value} {cur}";
    }

    /// <summary>
    /// Yield candidate balanced-bracket JSON spans from a possibly-markdown text, in priority
    /// order. Callers try to <c>JsonDocument.Parse</c> each in turn and keep the first parseable
    /// one. This is intentionally permissive: prose responses sometimes contain markdown links
    /// like <c>[label](url)</c> whose brackets balance but aren't JSON.
    /// </summary>
    private static IEnumerable<string> ExtractJsonSpans(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        // 1. Prefer explicit ```json / ``` fenced blocks — highest signal.
        var idx = 0;
        while (idx < text.Length)
        {
            var open = text.IndexOf("```", idx, StringComparison.Ordinal);
            if (open < 0) break;
            var afterOpen = open + 3;
            // Skip optional language tag up to end-of-line.
            var lineEnd = text.IndexOf('\n', afterOpen);
            if (lineEnd < 0) break;
            var contentStart = lineEnd + 1;
            var close = text.IndexOf("```", contentStart, StringComparison.Ordinal);
            if (close < 0) break;
            var fenced = text[contentStart..close].Trim();
            if (fenced.Length > 0 && (fenced[0] == '[' || fenced[0] == '{'))
                yield return fenced;
            idx = close + 3;
        }

        // 2. Fall back to every balanced-bracket span in the raw text. Prefer arrays, then objects.
        foreach (var span in AllBalancedSpans(text, '[', ']')) yield return span;
        foreach (var span in AllBalancedSpans(text, '{', '}')) yield return span;
    }

    private static IEnumerable<string> AllBalancedSpans(string text, char open, char close)
    {
        for (var start = 0; start < text.Length; start++)
        {
            if (text[start] != open) continue;
            var depth = 0;
            for (var i = start; i < text.Length; i++)
            {
                if (text[i] == open) depth++;
                else if (text[i] == close)
                {
                    depth--;
                    if (depth == 0)
                    {
                        yield return text.Substring(start, i - start + 1);
                        break;
                    }
                }
            }
        }
    }

    private async Task<List<AgentOption>?> TryRankWithLlmAsync(
        string prompt,
        UserContext? userContext,
        List<AgentOption> candidates,
        int maxOptions,
        CancellationToken ct)
    {
        var selectorAgentId = _foundryOptions.SelectorAgentId;

        var rankingPayload = new
        {
            instruction =
                "You are an aggregator. Given the user's original request and a list of candidate offers " +
                "returned by different specialist agents, pick the best " + maxOptions + " options overall. " +
                "Normalize them into a consistent shape. Respond ONLY with JSON of the form: " +
                "{\"options\":[{\"optionId\":\"<original>\",\"agentId\":\"<original>\",\"agentName\":\"<original>\"," +
                "\"title\":\"...\",\"description\":\"...\",\"price\":<number or null>,\"currency\":\"...\"," +
                "\"attributes\":{\"key\":\"value\"}}]}. Preserve the original optionId, agentId, and agentName exactly.",
            userPrompt = prompt,
            userPreferences = userContext?.Preferences ?? [],
            candidates = candidates.Select(c => new
            {
                optionId = c.OptionId,
                agentId = c.AgentId,
                agentName = c.AgentName,
                title = c.Title,
                description = c.Description,
                price = c.PriceValue,
                currency = c.Currency,
                attributes = c.Attributes
            })
        };

        try
        {
            var response = await _foundryAgentService.InvokeAgentAsync(
                selectorAgentId,
                JsonSerializer.Serialize(rankingPayload),
                ct: ct);

            var summary = response.TryGetValue("summary", out var sObj) ? sObj?.ToString() : null;
            if (string.IsNullOrWhiteSpace(summary)) return null;

            // The selector may wrap the ranked options in ```json fences or prose. Try each
            // parseable span until we find one whose root has an "options" array.
            string? optionsSpan = null;
            foreach (var span in ExtractJsonSpans(summary))
            {
                try
                {
                    using var probe = JsonDocument.Parse(span);
                    if (probe.RootElement.ValueKind == JsonValueKind.Object
                        && probe.RootElement.TryGetProperty("options", out _))
                    {
                        optionsSpan = span;
                        break;
                    }
                }
                catch (JsonException) { /* try next span */ }
            }
            if (optionsSpan is null) return null;

            using var doc = JsonDocument.Parse(optionsSpan);
            if (!doc.RootElement.TryGetProperty("options", out var opts) || opts.ValueKind != JsonValueKind.Array)
                return null;

            var byId = candidates.ToDictionary(c => c.OptionId, StringComparer.Ordinal);
            var ranked = new List<AgentOption>();
            foreach (var el in opts.EnumerateArray())
            {
                var origId = el.TryGetProperty("optionId", out var oidEl) && oidEl.ValueKind == JsonValueKind.String
                    ? oidEl.GetString() ?? string.Empty : string.Empty;

                // Anchor on the original candidate (so we keep its Raw agent output).
                if (!byId.TryGetValue(origId, out var anchor))
                    continue;

                anchor.Title = el.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String
                    ? tEl.GetString() : anchor.Title;
                anchor.Description = el.TryGetProperty("description", out var dEl) && dEl.ValueKind == JsonValueKind.String
                    ? dEl.GetString() : anchor.Description;
                if (el.TryGetProperty("price", out var pEl) && pEl.ValueKind == JsonValueKind.Number && pEl.TryGetDecimal(out var pd))
                {
                    anchor.PriceValue = pd;
                    anchor.Price = FormatPrice(pd, anchor.Currency);
                }
                anchor.Currency = el.TryGetProperty("currency", out var cEl) && cEl.ValueKind == JsonValueKind.String
                    ? cEl.GetString() : anchor.Currency;

                if (el.TryGetProperty("attributes", out var attrEl) && attrEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var attr in attrEl.EnumerateObject())
                    {
                        if (attr.Value.ValueKind == JsonValueKind.String)
                            anchor.Attributes[attr.Name] = attr.Value.GetString() ?? string.Empty;
                    }
                }

                anchor.Summary = anchor.Title ?? anchor.Description ?? anchor.Summary;

                // Recompose the flat wire fields after mutation so the LLM's normalized picks
                // appear in `details`/`price` too (not just internal state).
                anchor.Details = ComposeDetails(anchor.Title, anchor.Description, anchor.Attributes, anchor.Summary);
                if (anchor.PriceValue.HasValue)
                    anchor.Price = FormatPrice(anchor.PriceValue, anchor.Currency);

                ranked.Add(anchor);

                if (ranked.Count >= maxOptions) break;
            }

            return ranked.Count > 0 ? ranked : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "LLM-assisted option ranking via {AgentId} failed; falling back to heuristic candidates.",
                selectorAgentId);
            return null;
        }
    }

    private async Task<Dictionary<string, object>> ExecuteAgentActionAsync(
        Dictionary<string, object> parameters, CancellationToken ct)
    {
        // Prefer the session-store path: sessionId + optionId → (agentId, threadId).
        var prompt = parameters["prompt"]?.ToString() ?? string.Empty;
        var userContext = DeserializeParam<UserContext>(parameters["userContext"]);
        var sessionId = parameters.TryGetValue("sessionId", out var sidObj) ? sidObj?.ToString() ?? string.Empty : string.Empty;
        var optionId = parameters.TryGetValue("optionId", out var oidObj) ? oidObj?.ToString() ?? string.Empty : string.Empty;

        string agentId;
        string? threadId = null;

        var session = _sessionStore.Get(sessionId);
        var selected = session?.Options.FirstOrDefault(o =>
            string.Equals(o.OptionId, optionId, StringComparison.Ordinal));

        if (selected is not null)
        {
            agentId = selected.AgentId;
            threadId = string.IsNullOrEmpty(selected.ThreadId) ? null : selected.ThreadId;
            _logger.LogInformation(
                "Execute: routing to agent {AgentId} on thread {ThreadId} (session={SessionId}, option={OptionId})",
                agentId, threadId ?? "<new>", sessionId, optionId);
        }
        else
        {
            agentId = ExtractAgentIdFromPrompt(prompt);
            _logger.LogWarning(
                "Execute: no session/option match (session={SessionId}, option={OptionId}). Falling back to legacy prompt-embedded agentId {AgentId}.",
                sessionId, optionId, agentId);
        }

        var result = await _foundryAgentService.InvokeAgentAsync(
            agentId,
            prompt,
            parameters: new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["userId"] = userContext?.UserId ?? string.Empty,
                ["preferences"] = userContext?.Preferences ?? new Dictionary<string, string>()
            },
            threadId: threadId,
            ct: ct);

        // Persist the (possibly new) threadId back onto the option so subsequent Confirm continues the same conversation.
        if (selected is not null && result.TryGetValue("threadId", out var tidObj) && tidObj is string newTid && !string.IsNullOrEmpty(newTid))
        {
            selected.ThreadId = newTid;
            _sessionStore.Save(session!);
        }

        return result;
    }

    private async Task<Dictionary<string, object>> ExecuteConfirmBookingAsync(
        Dictionary<string, object> parameters, CancellationToken ct)
    {
        var prompt = parameters["prompt"]?.ToString() ?? string.Empty;
        var userContext = DeserializeParam<UserContext>(parameters["userContext"]);
        var sessionId = parameters.TryGetValue("sessionId", out var sidObj) ? sidObj?.ToString() ?? string.Empty : string.Empty;
        var optionId = parameters.TryGetValue("optionId", out var oidObj) ? oidObj?.ToString() ?? string.Empty : string.Empty;

        string agentId;
        string? threadId = null;

        var session = _sessionStore.Get(sessionId);
        var selected = session?.Options.FirstOrDefault(o =>
            string.Equals(o.OptionId, optionId, StringComparison.Ordinal));

        if (selected is not null)
        {
            agentId = selected.AgentId;
            threadId = string.IsNullOrEmpty(selected.ThreadId) ? null : selected.ThreadId;
        }
        else
        {
            agentId = ExtractAgentIdFromPrompt(prompt);
        }

        var confirmationPrompt = string.IsNullOrWhiteSpace(prompt)
            ? "Please confirm the booking with the details discussed above."
            : prompt;

        var result = await _foundryAgentService.InvokeAgentAsync(
            agentId,
            confirmationPrompt,
            parameters: new Dictionary<string, object>
            {
                ["action"] = "confirm",
                ["sessionId"] = sessionId,
                ["userId"] = userContext?.UserId ?? string.Empty
            },
            threadId: threadId,
            ct: ct);

        // Store preferences from this confirmed booking
        if (userContext != null)
        {
            await _userContextService.StoreInteractionAsync(
                userContext.UserId,
                "booking-confirmation",
                $"Confirmed via agent {agentId}: {confirmationPrompt}",
                ct: ct);
        }

        return result;
    }

    private async Task<bool> ExecuteStoreEmbeddingAsync(
        Dictionary<string, object> parameters, CancellationToken ct)
    {
        var userId = parameters["userId"]?.ToString() ?? string.Empty;
        var content = parameters["content"]?.ToString() ?? string.Empty;
        var domain = parameters["domain"]?.ToString() ?? "general";

        await _vectorStorage.StoreEmbeddingAsync(userId, content, domain, ct);
        return true;
    }

    private static double CalculateAgentScore(AgentInfo agent, string prompt, UserContext? context)
    {
        double score = 0;

        // Whole-token overlap (min length 3, skip stopwords) so "a" / "me" / "for" from the
        // prompt don't spuriously match every hyphenated agent name.
        var promptTokens = TokenizeMeaningful(prompt);
        var agentTokens = TokenizeMeaningful(
            $"{agent.Name} {agent.Description} {string.Join(' ', agent.Capabilities)}");

        foreach (var word in promptTokens)
        {
            if (agentTokens.Contains(word)) score += 0.5;
        }

        // Big domain-match bonus. If the prompt is "order a pizza" and the agent is
        // Dominos-agent, both classify to food-delivery — lift score decisively above unrelated
        // agents that only match on incidental tokens like "order".
        var (promptDomain, _) = ClassifyDomain(prompt);
        var (agentDomain, _) = ClassifyAgent(agent);
        if (!string.IsNullOrEmpty(promptDomain)
            && string.Equals(promptDomain, agentDomain, StringComparison.OrdinalIgnoreCase))
        {
            score += 3.0;
        }
        else if (!string.IsNullOrEmpty(promptDomain)
            && !string.IsNullOrEmpty(agentDomain)
            && !string.Equals(promptDomain, agentDomain, StringComparison.OrdinalIgnoreCase))
        {
            score -= 3.0; // hard penalty for cross-domain agents
        }

        // User preference boost — whole-token match only.
        if (context?.Preferences != null)
        {
            foreach (var pref in context.Preferences.Values)
            {
                var prefTokens = TokenizeMeaningful(pref?.ToString() ?? string.Empty);
                foreach (var pt in prefTokens)
                {
                    if (agentTokens.Contains(pt)) score += 0.4;
                }
            }
        }

        return score;
    }

    /// <summary>Whole-word tokenizer that lowercases, drops sub-3-char tokens, and filters common English stopwords.</summary>
    private static HashSet<string> TokenizeMeaningful(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tokenize(text))
        {
            if (t.Length < 3) continue;
            if (Stopwords.Contains(t)) continue;
            set.Add(t.ToLowerInvariant());
        }
        return set;
    }

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "you", "are", "can", "get", "was", "but", "not", "any", "has",
        "have", "had", "with", "from", "into", "that", "this", "those", "these", "there",
        "want", "need", "please", "just", "our", "your", "his", "her", "them", "they",
        "agent", "agents", "find", "help", "give"
    };

    /// <summary>
    /// Coarse domain classifier for both prompts and agents. Returns the highest-scoring
    /// domain (and its score); or (empty, 0) when no keyword matches at all.
    ///
    /// Scoring rules:
    ///   * Each PRIMARY keyword hit (concrete noun / brand name) weighs 3 points.
    ///   * Each SECONDARY keyword hit (generic verb / soft cue) weighs 1 point.
    /// This prevents ambiguous verbs like "order" from beating a strong brand signal like
    /// "Lenovo" — which is what caused a laptop agent to fan out for a food prompt.
    /// </summary>
    private static (string Domain, int Score) ClassifyDomain(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (string.Empty, 0);
        var tokens = TokenizeMeaningful(text);
        if (tokens.Count == 0) return (string.Empty, 0);

        (string Domain, int Score) best = (string.Empty, 0);
        foreach (var (domain, keywords) in DomainKeywords)
        {
            var primaryHits = keywords.Primary.Count(k => tokens.Contains(k));
            var secondaryHits = keywords.Secondary.Count(k => tokens.Contains(k));
            var score = (primaryHits * 3) + secondaryHits;

            // Only classify when we have at least ONE primary hit, OR two+ secondary hits.
            // A single generic verb ("order") on its own is not enough to claim a domain.
            var qualifies = primaryHits > 0 || secondaryHits >= 2;
            if (qualifies && score > best.Score) best = (domain, score);
        }
        return best;
    }

    private static (string Domain, int Score) ClassifyAgent(AgentInfo agent)
    {
        // Prefer the onboarded category tag when present — it's the most reliable signal.
        // The Agent Mesh onboarding UI persists the user-picked category as tags[0].
        if (agent.Configuration.TryGetValue("tags", out var tagsObj) && tagsObj is IEnumerable<object> tagsEnum)
        {
            foreach (var t in tagsEnum)
            {
                var tag = t?.ToString();
                if (string.IsNullOrWhiteSpace(tag)) continue;
                var (mapped, _) = ClassifyDomain(tag);
                if (!string.IsNullOrEmpty(mapped)) return (mapped, 100);
            }
        }

        // Otherwise classify from name + description + capabilities.
        var blob = $"{agent.Name} {agent.Description} {string.Join(' ', agent.Capabilities)}";
        return ClassifyDomain(blob);
    }

    /// <summary>
    /// Keyword-to-domain map, split into PRIMARY (concrete nouns / brand names — strong signal,
    /// weight 3) and SECONDARY (generic verbs / soft cues — weight 1). Splitting prevents an
    /// ambiguous verb like "order" from letting a Lenovo agent classify as food-delivery just
    /// because its description happens to say "help you order the right laptop".
    /// </summary>
    private static readonly Dictionary<string, (string[] Primary, string[] Secondary)> DomainKeywords
        = new(StringComparer.OrdinalIgnoreCase)
    {
        ["flights"] = (
            Primary: new[]
            {
                "flight", "flights", "airline", "airlines", "airfare", "airplane", "aircraft",
                "airport", "boarding", "ticket", "tickets",
                "emirates", "airindia", "indigo", "lufthansa", "delta", "jetblue",
                "southwest", "qatar", "etihad", "cathay"
            },
            Secondary: new[] { "fly", "flying", "plane" }
        ),
        ["hotels"] = (
            Primary: new[]
            {
                "hotel", "hotels", "accommodation", "lodging", "motel", "resort",
                "hilton", "marriott", "hyatt", "sheraton", "airbnb", "oyo"
            },
            Secondary: new[] { "stay", "suite", "checkin", "checkout" }
        ),
        ["food-delivery"] = (
            Primary: new[]
            {
                "pizza", "pizzas", "burger", "burgers", "sushi", "biryani", "noodles", "pasta",
                "sandwich", "food", "meal", "snack", "cuisine", "dessert", "salad", "ramen",
                "curry", "taco", "tacos", "sub", "wrap", "wings", "chinese", "indian", "italian",
                "mexican", "thai", "japanese",
                "dominos", "pizzahut", "mcdonalds", "kfc", "subway", "chipotle", "starbucks",
                "ubereats", "doordash", "grubhub", "swiggy", "zomato",
                // Common intent verbs promoted to primary — "eat/eating/hungry" should classify
                // food even without a specific cuisine keyword.
                "eat", "eating", "hungry", "craving"
            },
            Secondary: new[] { "delivery", "deliver", "takeout", "takeaway", "order", "ordering" }
        ),
        ["dining"] = (
            Primary: new[]
            {
                "restaurant", "restaurants", "reservation", "reservations",
                "opentable", "resy",
                "dinner", "lunch", "breakfast", "brunch"
            },
            Secondary: new[] { "dine", "dining", "table", "tables" }
        ),
        ["electronics"] = (
            Primary: new[]
            {
                // Laptops / computers
                "laptop", "laptops", "notebook", "notebooks", "ultrabook", "chromebook", "macbook",
                "computer", "computers", "desktop", "desktops",
                // Gaming / hardware specs
                "rtx", "gtx", "rog", "ryzen", "nvidia",
                "gpu", "cpu", "ssd", "144hz", "165hz", "240hz",
                // Consumer electronics
                "smartphone", "iphone", "android", "pixel", "samsung", "galaxy",
                "tablet", "tablets", "ipad",
                "monitor", "monitors", "keyboard", "headphone", "headphones",
                "earbuds", "airpods", "console", "playstation", "xbox", "nintendo",
                // Storefronts / brands
                "bestbuy", "newegg", "microcenter", "flipkart",
                "dell", "hp", "lenovo", "asus", "acer", "msi", "alienware",
                "omen", "legion", "victus", "predator"
            },
            Secondary: new[] { "gaming", "gamer", "intel", "graphics", "ram", "phone", "phones", "display", "mouse", "speaker", "speakers", "switch" }
        )
    };

    private static string ExtractAgentIdFromPrompt(string prompt)
    {
        // Expected format in execution: "agentId:xxx|remaining prompt"
        if (prompt.StartsWith("agentId:", StringComparison.OrdinalIgnoreCase))
        {
            var pipeIdx = prompt.IndexOf('|');
            return pipeIdx > 8 ? prompt[8..pipeIdx] : prompt[8..];
        }
        return prompt;
    }

    private static string ExtractDomainFromPrompt(string prompt) => ClassifyDomain(prompt).Domain;

    private static T? DeserializeParam<T>(object value)
    {
        if (value is T typed) return typed;
        if (value is JsonElement element)
            return JsonSerializer.Deserialize<T>(element.GetRawText());
        var json = JsonSerializer.Serialize(value);
        return JsonSerializer.Deserialize<T>(json);
    }
}
