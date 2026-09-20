using System.Text.RegularExpressions;
using OrchestratorEngine.Api.Models;
using OrchestratorEngine.Api.Services;
using OrchestratorEngine.Api.Workflows.Skills;

namespace OrchestratorEngine.Api.Workflows;

public sealed class WorkflowEngine : IWorkflowEngine
{
    private readonly ISkillExecutor _skillExecutor;
    private readonly IFoundryAgentService _foundryAgentService;
    private readonly IUserContextService _userContextService;
    private readonly ISessionStore _sessionStore;
    private readonly ILogger<WorkflowEngine> _logger;

    // Cap concurrent fan-out to keep Foundry rate limits happy. Tune via config later if needed.
    // Kept in step with MaxAgentsForGenericFanout below so a fully-filled fan-out doesn't
    // silently serialize the tail agent (previously 4 threads for a 5-agent fan-out).
    private const int MaxParallelAgentInvocations = 5;

    // Upper bound on how many same-domain agents we invoke for a generic (no explicit brand)
    // query. Matches SkillExecutor.ExecuteSelectBestAgentAsync's positive.Take(5) so we don't
    // trim off ranker output at the fan-out gate — that mismatch is what caused a 4th
    // same-score flight agent (Qatar Airways) to drop even though the ranker had accepted it.
    private const int MaxAgentsForGenericFanout = 5;

    // Per-agent option quota when running a generic-query fan-out. Total options surfaced to
    // the user = MaxAgentsForGenericFanout * MaxOptionsPerAgentGeneric (5 * 3 = 15) before the
    // ranker's own maxAgents cap kicks in.
    private const int MaxOptionsPerAgentGeneric = 3;

    public WorkflowEngine(
        ISkillExecutor skillExecutor,
        IFoundryAgentService foundryAgentService,
        IUserContextService userContextService,
        ISessionStore sessionStore,
        ILogger<WorkflowEngine> logger)
    {
        _skillExecutor = skillExecutor;
        _foundryAgentService = foundryAgentService;
        _userContextService = userContextService;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    // ==========================================================================================
    //  DISCOVER
    // ==========================================================================================
    public async Task<OrchestrationResponse> DiscoverAndRecommendAsync(
        string prompt, UserContext userContext, string sessionId, string? preferredAgentId = null, CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Discover workflow started for session {SessionId} (preferredAgentId={PreferredAgentId})",
            sessionId, preferredAgentId ?? "-");

        var previousSession = _sessionStore.Get(sessionId);

        // ------------------------------------------------------------------------------------
        // Address slot handling — must run BEFORE domain fan-out so we don't burn agent turns
        // asking "what's your address?" repeatedly.
        //
        // When the session has a PendingPrompt (we're WAITING for an address) we interpret
        // the reply liberally:
        //   1. Cancellation  → clear parked, tell user we abandoned it.
        //   2. New task      → clear parked, fall through to normal Discover on the new prompt.
        //   3. Affirmation + we have a saved/pending address → accept it and resume.
        //   4. Anything else → treat as the address itself (save + resume). We deliberately
        //      do NOT gate this on LooksLikeAddress — many valid addresses are landmarks,
        //      neighborhoods, or non-US formats that our regex won't match, and forcing a
        //      strict match here caused the reply to fall through into Discover as a brand
        //      new prompt and fan out to unrelated agents.
        // ------------------------------------------------------------------------------------
        if (previousSession is not null && !string.IsNullOrWhiteSpace(previousSession.PendingPrompt))
        {
            var parkedPrompt = previousSession.PendingPrompt!;

            if (LooksLikeCancellation(prompt))
            {
                previousSession.PendingPrompt = null;
                previousSession.AddressConfirmed = false;
                _sessionStore.Save(previousSession);
                _logger.LogInformation(
                    "Discover: user cancelled the address ask; dropping parked prompt.");
                return new OrchestrationResponse
                {
                    SessionId = sessionId,
                    Status = OrchestrationStatus.Failed,
                    Message = "Okay, I've cancelled that request. Let me know what you'd like to do next.",
                    Metadata = new Dictionary<string, object> { ["reason"] = "address-ask-cancelled" }
                };
            }

            if (LooksLikeNewTask(prompt, previousSession.PromptDomain))
            {
                previousSession.PendingPrompt = null;
                _sessionStore.Save(previousSession);
                _logger.LogInformation(
                    "Discover: user started a NEW task while an address ask was pending; abandoning parked prompt '{Parked}'.",
                    parkedPrompt);
                // Fall through to normal Discover on the new prompt.
            }
            else if (LooksLikeAffirmation(prompt))
            {
                var savedForAffirm = !string.IsNullOrWhiteSpace(previousSession.DeliveryAddress)
                    ? previousSession.DeliveryAddress
                    : await _userContextService.GetSavedAddressAsync(userContext.UserId, ct);

                if (!string.IsNullOrWhiteSpace(savedForAffirm))
                {
                    previousSession.DeliveryAddress = savedForAffirm;
                    previousSession.AddressConfirmed = true;
                    previousSession.PendingPrompt = null;
                    _sessionStore.Save(previousSession);
                    _logger.LogInformation(
                        "Discover: user affirmed saved address; resuming parked prompt.");
                    return await DiscoverAndRecommendAsync(parkedPrompt, userContext, sessionId, preferredAgentId, ct);
                }
                // No saved address to affirm — fall through, re-ask below.
            }
            else
            {
                // Treat the entire reply as the delivery address.
                var addr = prompt.Trim();
                await _userContextService.SaveAddressAsync(userContext.UserId, addr, ct);
                previousSession.DeliveryAddress = addr;
                previousSession.AddressConfirmed = true;
                previousSession.PendingPrompt = null;
                _sessionStore.Save(previousSession);
                _logger.LogInformation(
                    "Discover: captured delivery address for user {UserId}; resuming parked prompt.",
                    userContext.UserId);
                return await DiscoverAndRecommendAsync(parkedPrompt, userContext, sessionId, preferredAgentId, ct);
            }
        }

        var promptDomain = ExtractDomain(prompt);
        var needsAddress = RequiresDeliveryAddress(prompt, promptDomain);
        string? deliveryAddress = null;
        var addressAlreadyConfirmed = previousSession?.AddressConfirmed ?? false;

        // ------------------------------------------------------------------------------------
        // Conversational short-circuit — meta questions ("what's my address?", "recap those
        // options"), greetings, help. These are NOT agent tasks and must not fan out.
        // Runs AFTER the address-slot block (so an address reply still wins) but BEFORE the
        // domain classifier / fan-out, so a "what's my address" query never touches Foundry.
        // ------------------------------------------------------------------------------------
        var convIntent = ClassifyConversationalIntent(prompt, previousSession);
        if (convIntent != ConversationalIntent.None)
        {
            var reply = await BuildConversationalReplyAsync(convIntent, previousSession, userContext, ct);
            _logger.LogInformation(
                "Discover: conversational intent {Intent} handled locally; skipping agent fan-out.", convIntent);
            return new OrchestrationResponse
            {
                SessionId = sessionId,
                Status = OrchestrationStatus.Informational,
                Message = reply,
                Metadata = new Dictionary<string, object>
                {
                    ["conversational"] = true,
                    ["intent"] = convIntent.ToString()
                }
            };
        }

        if (needsAddress)
        {
            // Silently resolve the saved delivery address so it can flow into the agent's
            // enriched prompt — we no longer park the turn or ask the user for it. If we
            // don't have one on file the agent is expected to use its own default flow.
            // Prefer the session's in-memory address first (already resolved on a previous
            // turn) to avoid Azure Cognitive Search's eventual-consistency window.
            deliveryAddress = !string.IsNullOrWhiteSpace(previousSession?.DeliveryAddress)
                ? previousSession!.DeliveryAddress
                : await _userContextService.GetSavedAddressAsync(userContext.UserId, ct);

            if (previousSession is not null && !string.IsNullOrWhiteSpace(deliveryAddress))
            {
                previousSession.DeliveryAddress = deliveryAddress;
                previousSession.AddressConfirmed = true;
                previousSession.PromptDomain = promptDomain;
                _sessionStore.Save(previousSession);
            }
        }

        // ------------------------------------------------------------------------------------
        // Continuity classification — is this a refinement of the last turn or a new intent?
        // ------------------------------------------------------------------------------------
        var continuation = ClassifyContinuation(prompt, promptDomain, previousSession);

        // Solo-agent mode override: when the client sends preferredAgentId ("Chat with agent"
        // in the sample UI), we route the prompt to that agent alone. If the agent already
        // has options in this session we treat it as a refinement so its Foundry thread is
        // reused; otherwise it's a new intent scoped to that agent.
        if (!string.IsNullOrWhiteSpace(preferredAgentId))
        {
            var hasHistoryForAgent = previousSession is not null
                && previousSession.Options.Any(o =>
                    string.Equals(o.AgentId, preferredAgentId, StringComparison.Ordinal));
            continuation = hasHistoryForAgent
                ? ContinuationDecision.RefinePrevious
                : ContinuationDecision.NewIntent;
            _logger.LogInformation(
                "Discover: preferredAgentId={AgentId} forced continuation={Continuation} (hasHistory={HasHistory})",
                preferredAgentId, continuation, hasHistoryForAgent);
        }

        _logger.LogInformation(
            "Discover: continuation decision for session {SessionId} = {Decision} (prevDomain={Prev}, newDomain={Cur}, prevOptions={Opt})",
            sessionId, continuation, previousSession?.PromptDomain ?? "-", promptDomain, previousSession?.Options.Count ?? 0);

        // Step 1: pick agents. On refinement we reuse the previous session's agents (and threads).
        List<AgentInfo> selectedAgents = [];
        List<string> preferredBrands = [];
        List<string> previouslyShownSummaries = [];
        if (continuation == ContinuationDecision.RefinePrevious && previousSession is not null)
        {
            selectedAgents = previousSession.Options
                .GroupBy(o => o.AgentId, StringComparer.Ordinal)
                .Select(g => new AgentInfo { AgentId = g.Key, Name = g.First().AgentName })
                .ToList();

            // Solo-agent mode: keep only the requested agent so brand extraction and
            // fan-out below can't accidentally re-include the others.
            if (!string.IsNullOrWhiteSpace(preferredAgentId))
            {
                selectedAgents = selectedAgents
                    .Where(a => string.Equals(a.AgentId, preferredAgentId, StringComparison.Ordinal))
                    .ToList();
            }

            if (selectedAgents.Count == 0)
                continuation = ContinuationDecision.NewIntent;

            // Brand preference extraction — when the user names a specific provider (airline,
            // restaurant, courier, hotel chain) in the refinement, restrict fan-out to agents
            // whose name matches. This prevents unrelated agents from returning off-brand
            // options that then win the ranker on volume alone.
            preferredBrands = ExtractBrandPreferences(prompt, selectedAgents);
            if (preferredBrands.Count > 0)
            {
                var filtered = selectedAgents
                    .Where(a => AgentMatchesAnyBrand(a.Name, a.AgentId, preferredBrands))
                    .ToList();

                _logger.LogInformation(
                    "Discover refinement: brand preference(s) [{Brands}] narrowed agents {Before} → {After}",
                    string.Join(", ", preferredBrands), selectedAgents.Count, filtered.Count);

                if (filtered.Count == 0)
                {
                    return new OrchestrationResponse
                    {
                        SessionId = sessionId,
                        Status = OrchestrationStatus.Failed,
                        Message =
                            $"I don't have an agent for {string.Join(" / ", preferredBrands)} available for this request. " +
                            "Would you like me to look at other providers instead?",
                        Metadata = new Dictionary<string, object>
                        {
                            ["reason"] = "no-matching-brand-agent",
                            ["preferredBrands"] = preferredBrands,
                            ["promptDomain"] = promptDomain
                        }
                    };
                }

                selectedAgents = filtered;
            }

            // Preserve previously shown option summaries so the refinement prompt can ask
            // for "more" without the agent repeating the same offers.
            previouslyShownSummaries = previousSession.Options
                .Select(o => o.Summary)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        if (continuation == ContinuationDecision.NewIntent)
        {
            var agents = await _skillExecutor.ExecuteAsync<List<AgentInfo>>(
                SkillNames.FetchAvailableAgents,
                new Dictionary<string, object> { ["prompt"] = prompt },
                ct);

            if (agents is null || agents.Count == 0)
            {
                // FetchAvailableAgents already filters by the classified prompt domain, so an
                // empty list here means we recognized what the user wanted (e.g. "laptop" →
                // electronics) but no onboarded agent covers that domain. Return a clear
                // message rather than silently falling back to unrelated agents.
                var friendlyDomain = FormatDomainLabel(promptDomain);
                var domainMsg = string.IsNullOrWhiteSpace(friendlyDomain) || friendlyDomain == "general"
                    ? "No relevant agent is available for your request. Try onboarding an agent that covers this task."
                    : $"No relevant agent is available for {friendlyDomain} requests. Try onboarding a {friendlyDomain} agent, or rephrase your request.";

                return new OrchestrationResponse
                {
                    SessionId = sessionId,
                    Status = OrchestrationStatus.Failed,
                    Message = domainMsg,
                    Metadata = new Dictionary<string, object>
                    {
                        ["reason"] = "no-agent-for-domain",
                        ["promptDomain"] = promptDomain
                    }
                };
            }

            // Solo-agent mode: skip SelectBestAgent and route to the requested agent only.
            if (!string.IsNullOrWhiteSpace(preferredAgentId))
            {
                var pinned = agents
                    .Where(a => string.Equals(a.AgentId, preferredAgentId, StringComparison.Ordinal))
                    .ToList();
                if (pinned.Count == 0)
                {
                    return new OrchestrationResponse
                    {
                        SessionId = sessionId,
                        Status = OrchestrationStatus.Failed,
                        Message = "The agent you're chatting with isn't available for that request.",
                        Metadata = new Dictionary<string, object>
                        {
                            ["reason"] = "preferred-agent-unavailable",
                            ["preferredAgentId"] = preferredAgentId
                        }
                    };
                }
                selectedAgents = pinned;
            }
            else
            {
                selectedAgents = await _skillExecutor.ExecuteAsync<List<AgentInfo>>(
                    SkillNames.SelectBestAgent,
                    new Dictionary<string, object>
                    {
                        ["prompt"] = prompt,
                        ["agents"] = agents,
                        ["userContext"] = userContext
                    },
                    ct) ?? [];
            }

            if (selectedAgents.Count == 0)
            {
                var friendlyDomain = FormatDomainLabel(promptDomain);
                var domainMsg = string.IsNullOrWhiteSpace(friendlyDomain) || friendlyDomain == "general"
                    ? "No relevant agent is available for your request."
                    : $"No relevant agent is available for {friendlyDomain} requests. Try onboarding a {friendlyDomain} agent, or rephrase your request.";

                return new OrchestrationResponse
                {
                    SessionId = sessionId,
                    Status = OrchestrationStatus.Failed,
                    Message = domainMsg,
                    Metadata = new Dictionary<string, object>
                    {
                        ["reason"] = "no-agent-for-domain",
                        ["promptDomain"] = promptDomain
                    }
                };
            }

            // On a NEW intent, also honor an explicit brand mention in the prompt
            // ("book me an Emirates flight"). When the user names a specific provider we
            // narrow fan-out to those agents so the response isn't diluted by competitors.
            preferredBrands = ExtractBrandPreferences(prompt, selectedAgents);
            if (preferredBrands.Count > 0)
            {
                var brandFiltered = selectedAgents
                    .Where(a => AgentMatchesAnyBrand(a.Name, a.AgentId, preferredBrands))
                    .ToList();

                _logger.LogInformation(
                    "Discover new-intent: brand preference(s) [{Brands}] narrowed agents {Before} -> {After}",
                    string.Join(", ", preferredBrands), selectedAgents.Count, brandFiltered.Count);

                if (brandFiltered.Count > 0)
                    selectedAgents = brandFiltered;
            }
        }

        // Decide the fan-out shape.
        //   * Generic query (no explicit brand)         -> up to MaxAgentsForGenericFanout agents,
        //                                                 up to MaxOptionsPerAgentGeneric options each.
        //   * Brand-specific / refinement continuation  -> top 3 options overall (legacy behavior).
        var isGenericQuery = preferredBrands.Count == 0;
        var maxAgentsForFanout = isGenericQuery ? MaxAgentsForGenericFanout : selectedAgents.Count;
        var maxOptionsPerAgent = isGenericQuery ? MaxOptionsPerAgentGeneric : 0; // 0 = no per-agent quota
        var maxTotalOptions = isGenericQuery ? maxAgentsForFanout * maxOptionsPerAgent : 3;

        if (selectedAgents.Count > maxAgentsForFanout)
        {
            _logger.LogInformation(
                "Discover: generic-query fan-out capped at {Cap} agents (had {Total}). Dropped=[{Dropped}]",
                maxAgentsForFanout, selectedAgents.Count,
                string.Join(", ", selectedAgents.Skip(maxAgentsForFanout).Select(a => $"'{a.Name}'")));
            selectedAgents = selectedAgents.Take(maxAgentsForFanout).ToList();
        }

        _logger.LogInformation(
            "Discover: fanning out to {Count} agent(s): [{Names}]",
            selectedAgents.Count,
            string.Join(", ", selectedAgents.Select(a => $"'{a.Name}'(id={a.AgentId})")));

        // Step 2: enriched prompt with refinement + delivery address context.
        var contextDocs = await _userContextService.GetRelevantContextAsync(
            userContext.UserId, prompt, ct);

        var enrichedPrompt = BuildEnrichedPrompt(
            prompt,
            userContext,
            contextDocs,
            deliveryAddress: needsAddress ? deliveryAddress : null,
            refinementOf: continuation == ContinuationDecision.RefinePrevious ? previousSession?.OriginalPrompt : null,
            preferredBrands: preferredBrands,
            previouslyShownSummaries: previouslyShownSummaries);

        // Step 3: fan out. On refinement, continue on each agent's existing threadId.
        var threadByAgent = continuation == ContinuationDecision.RefinePrevious && previousSession is not null
            ? previousSession.Options
                .Where(o => !string.IsNullOrWhiteSpace(o.ThreadId))
                .GroupBy(o => o.AgentId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().ThreadId, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        using var throttle = new SemaphoreSlim(MaxParallelAgentInvocations);
        var invocationTasks = selectedAgents.Select(async agent =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var tid = threadByAgent.TryGetValue(agent.AgentId, out var t) ? t : null;
                var raw = await _foundryAgentService.InvokeAgentAsync(agent.AgentId, enrichedPrompt, threadId: tid, ct: ct);
                raw["agentName"] = agent.Name;
                return raw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Agent {AgentId} invocation threw during fan-out; producing error placeholder.",
                    agent.AgentId);
                return new Dictionary<string, object>
                {
                    ["agentId"] = agent.AgentId,
                    ["agentName"] = agent.Name,
                    ["status"] = "failed",
                    ["summary"] = $"Invocation error: {ex.Message}"
                };
            }
            finally
            {
                throttle.Release();
            }
        });

        var rawResults = (await Task.WhenAll(invocationTasks)).ToList();

        // Per-agent outcome dump so a missing option can be traced back to its invoke status.
        // Without this, an agent that came back with status="requires_action" or an empty
        // summary looks identical to one that was never invoked — both simply "don't show up".
        foreach (var raw in rawResults)
        {
            var agentName = raw.TryGetValue("agentName", out var an) ? an?.ToString() : null;
            var agentId = raw.TryGetValue("agentId", out var ai) ? ai?.ToString() : null;
            var status = raw.TryGetValue("status", out var st) ? st?.ToString() : null;
            var summary = raw.TryGetValue("summary", out var su) ? su?.ToString() ?? string.Empty : string.Empty;
            var errCode = raw.TryGetValue("errorCode", out var ec) ? ec?.ToString() : null;
            var errMsg = raw.TryGetValue("error", out var em) ? em?.ToString() : null;
            _logger.LogInformation(
                "Discover fan-out result: name='{Name}' id={Id} status={Status} summaryLen={Len} errorCode={ErrCode} error='{Err}'",
                agentName, agentId, status, summary.Length,
                errCode ?? "(none)", errMsg ?? "(none)");
        }

        // Step 4: rank + normalize.
        var rankedOptions = await _skillExecutor.ExecuteAsync<List<AgentOption>>(
            SkillNames.RankOptions,
            new Dictionary<string, object>
            {
                ["prompt"] = prompt,
                ["userContext"] = userContext,
                ["rawResults"] = rawResults,
                ["maxOptions"] = maxTotalOptions,
                ["maxOptionsPerAgent"] = maxOptionsPerAgent,
                ["maxAgents"] = maxAgentsForFanout,
                ["promptDomain"] = promptDomain
            },
            ct) ?? [];

        // Step 5: persist session state.
        var sessionOptions = rankedOptions.Select(o => new SessionOption
        {
            OptionId = o.OptionId,
            AgentId = o.AgentId,
            AgentName = o.AgentName,
            ThreadId = o.Raw.TryGetValue("threadId", out var tid) ? tid?.ToString() ?? string.Empty : string.Empty,
            Surface = o.Raw.TryGetValue("surface", out var srf) ? srf?.ToString() ?? string.Empty : string.Empty,
            Summary = o.Summary,
            RawContent = o.Raw.TryGetValue("content", out var c) ? c?.ToString() ?? string.Empty : string.Empty
        }).ToList();

        _sessionStore.Save(new SessionState
        {
            SessionId = sessionId,
            UserId = userContext.UserId,
            OriginalPrompt = continuation == ContinuationDecision.RefinePrevious && previousSession is not null
                ? previousSession.OriginalPrompt
                : prompt,
            PromptDomain = promptDomain,
            Options = sessionOptions,
            DeliveryAddress = deliveryAddress,
            AddressConfirmed = addressAlreadyConfirmed
        });

        var metadata = new Dictionary<string, object>
        {
            ["agentsInvoked"] = selectedAgents.Count,
            ["rawResultCount"] = rawResults.Count,
            ["promptDomain"] = promptDomain,
            ["continuation"] = continuation.ToString()
        };
        if (!string.IsNullOrWhiteSpace(deliveryAddress))
        {
            metadata["deliveryAddress"] = deliveryAddress;
            metadata["addressConfirmed"] = addressAlreadyConfirmed;
        }

        // When no options came back, look at the raw fan-out results to explain WHY. This turns
        // a bare "no results" into an actionable message like "Both electronics agents replied
        // but had no matches in stock", which is what the user will actually see for a laptop
        // prompt that both Dell and Lenovo answered with an empty JSON array.
        var failMessage = "No agent returned a usable result. Try refining your request.";
        if (rankedOptions.Count == 0 && rawResults.Count > 0)
        {
            var okReplies = rawResults.Count(r =>
                r.TryGetValue("status", out var st) && st?.ToString() == "completed");
            var failReplies = rawResults.Count - okReplies;
            var invokedAgents = string.Join(", ", rawResults
                .Select(r => r.TryGetValue("agentName", out var an) ? an?.ToString() : null)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => $"'{n}'"));
            failMessage = okReplies > 0
                ? $"Contacted {rawResults.Count} agent(s) ({invokedAgents}) but none had a concrete offer for this request. Try refining with brand, budget, or specs."
                : $"All {rawResults.Count} agent(s) ({invokedAgents}) failed to respond. Try again in a moment.";
        }

        return new OrchestrationResponse
        {
            SessionId = sessionId,
            Status = rankedOptions.Count > 0 ? OrchestrationStatus.OptionsAvailable : OrchestrationStatus.Failed,
            Message = rankedOptions.Count > 0
                ? (continuation == ContinuationDecision.RefinePrevious
                    ? $"Updated — {rankedOptions.Count} option(s) based on your refinement."
                    : $"Found {rankedOptions.Count} option(s) for your request.")
                : failMessage,
            Options = rankedOptions,
            Metadata = metadata
        };
    }

    // ==========================================================================================
    //  EXECUTE — takes prep-action on the user's behalf with strict guardrails.
    // ==========================================================================================
    public async Task<OrchestrationResponse> ExecuteSelectionAsync(
        string prompt, UserContext userContext, string sessionId, string? optionId, CancellationToken ct = default)
    {
        _logger.LogInformation("Execute selection workflow for session {SessionId}, option {OptionId}", sessionId, optionId);

        var session = _sessionStore.Get(sessionId);
        var selected = session?.Options.FirstOrDefault(o =>
            string.Equals(o.OptionId, optionId, StringComparison.Ordinal));

        // Delivery-address gate removed — the saved address is applied silently below
        // via `effectiveAddress`, so the user is never prompted at Execute time.

        // Guardrail decision.
        var guardrail = EvaluateExecuteGuardrails(session);

        // Real address + preference context so the agent doesn't invent them. We prefer the
        // session's snapshot (already confirmed) and only fall back to the vector store when
        // the session is missing a value — this avoids Azure Search's eventual-consistency
        // window immediately after a save.
        var effectiveAddress = !string.IsNullOrWhiteSpace(session?.DeliveryAddress)
            ? session!.DeliveryAddress
            : await _userContextService.GetSavedAddressAsync(userContext.UserId, ct);
        var isDeliveryDomain = session is not null
            && RequiresDeliveryAddress(session.OriginalPrompt, session.PromptDomain);

        var executePrompt = ComposeExecutePrompt(
            userPrompt: prompt,
            selected: selected,
            guardrail: guardrail,
            promptDomain: session?.PromptDomain ?? string.Empty,
            deliveryAddress: isDeliveryDomain ? effectiveAddress : null,
            userPreferences: userContext.Preferences);

        var executionResult = await _skillExecutor.ExecuteAsync<Dictionary<string, object>>(
            SkillNames.ExecuteAgentAction,
            new Dictionary<string, object>
            {
                ["prompt"] = executePrompt,
                ["userContext"] = userContext,
                ["sessionId"] = sessionId,
                ["optionId"] = optionId ?? string.Empty
            },
            ct);

        if (executionResult is null)
        {
            return new OrchestrationResponse
            {
                SessionId = sessionId,
                Status = OrchestrationStatus.Failed,
                Message = "Failed to execute the selected option."
            };
        }

        var summary = executionResult.TryGetValue("summary", out var s) ? s?.ToString() ?? string.Empty : string.Empty;

        // Parse the agent's structured preview (JSON) into normalized fields the client can
        // render directly — title/description/price/address/payment/details. When the agent
        // fails to produce JSON we still surface whatever text it did return via `summary`.
        var preview = ParseExecutePreview(summary);

        // Guarantee address & preferences echo through even if the agent forgot to include
        // them. The client card should never fall back to hardcoded values.
        if (isDeliveryDomain && !string.IsNullOrWhiteSpace(effectiveAddress)
            && !preview.ContainsKey("delivery_address"))
        {
            preview["delivery_address"] = effectiveAddress!;
        }
        if (!preview.ContainsKey("payment_method")
            && userContext.Preferences.TryGetValue("payment_method", out var pm)
            && !string.IsNullOrWhiteSpace(pm))
        {
            preview["payment_method"] = pm;
        }
        if (selected is not null)
        {
            preview.TryAdd("selected_title", selected.Summary ?? string.Empty);
        }

        var message = guardrail.RequiresConfirmation
            ? $"I've prepared this on your behalf but need your explicit confirmation before I commit — {guardrail.Reason} Click **Confirm booking** to finalize."
            : "Done — completed on your behalf.";

        var confirmationData = new Dictionary<string, object>(executionResult, StringComparer.Ordinal);
        foreach (var kv in preview)
            confirmationData[kv.Key] = kv.Value;

        return new OrchestrationResponse
        {
            SessionId = sessionId,
            Status = guardrail.RequiresConfirmation
                ? OrchestrationStatus.AwaitingSelection
                : OrchestrationStatus.Confirmed,
            Message = message,
            Confirmation = new ConfirmationDetails
            {
                AgentId = executionResult.TryGetValue("agentId", out var agentId)
                    ? agentId?.ToString() ?? string.Empty : string.Empty,
                Provider = executionResult.TryGetValue("provider", out var provider)
                    ? provider?.ToString() ?? string.Empty : string.Empty,
                Summary = summary,
                Data = confirmationData
            },
            Metadata = new Dictionary<string, object>
            {
                ["requiresConfirmation"] = guardrail.RequiresConfirmation,
                ["guardrailReason"] = guardrail.Reason,
                ["autoExecuted"] = !guardrail.RequiresConfirmation,
                ["deliveryAddress"] = effectiveAddress ?? string.Empty,
                ["promptDomain"] = session?.PromptDomain ?? string.Empty
            }
        };
    }

    // ==========================================================================================
    //  CONFIRM
    // ==========================================================================================
    public async Task<OrchestrationResponse> ConfirmAndFinalizeAsync(
        string prompt, UserContext userContext, string sessionId, string? optionId, CancellationToken ct = default)
    {
        _logger.LogInformation("Confirm workflow for session {SessionId}, option {OptionId}", sessionId, optionId);

        var session = _sessionStore.Get(sessionId);

        // Delivery-address gate on confirm too — no commit until address is confirmed.
        if (session is not null
            && RequiresDeliveryAddress(session.OriginalPrompt, session.PromptDomain)
            && !session.AddressConfirmed)
        {
            var saved = await _userContextService.GetSavedAddressAsync(userContext.UserId, ct);
            session.DeliveryAddress = saved;
            session.PendingPrompt = session.OriginalPrompt;
            _sessionStore.Save(session);

            return new OrchestrationResponse
            {
                SessionId = sessionId,
                Status = OrchestrationStatus.NeedsAddress,
                Message = string.IsNullOrWhiteSpace(saved)
                    ? "Before I confirm this order, please share a delivery address."
                    : $"Before I confirm this order, is this still your delivery address?\n\n  {saved}\n\nReply \"yes\" to use it, or send a new address.",
                Metadata = new Dictionary<string, object>
                {
                    ["reason"] = string.IsNullOrWhiteSpace(saved) ? "delivery-requires-address" : "confirm-saved-address",
                    ["deliveryAddress"] = saved ?? string.Empty
                }
            };
        }

        var confirmationResult = await _skillExecutor.ExecuteAsync<Dictionary<string, object>>(
            SkillNames.ConfirmBooking,
            new Dictionary<string, object>
            {
                ["prompt"] = prompt,
                ["userContext"] = userContext,
                ["sessionId"] = sessionId,
                ["optionId"] = optionId ?? string.Empty
            },
            ct);

        if (confirmationResult is null)
        {
            return new OrchestrationResponse
            {
                SessionId = sessionId,
                Status = OrchestrationStatus.Failed,
                Message = "Failed to confirm the booking."
            };
        }

        await _userContextService.StoreInteractionAsync(
            userContext.UserId,
            "confirmed-booking",
            $"Confirmed: {prompt}",
            ct: ct);

        return new OrchestrationResponse
        {
            SessionId = sessionId,
            Status = OrchestrationStatus.Confirmed,
            Message = "Booking confirmed successfully.",
            Confirmation = new ConfirmationDetails
            {
                ConfirmationId = confirmationResult.TryGetValue("confirmationId", out var confId)
                    ? confId?.ToString() ?? Guid.NewGuid().ToString("N")
                    : Guid.NewGuid().ToString("N"),
                AgentId = confirmationResult.TryGetValue("agentId", out var agentId)
                    ? agentId?.ToString() ?? string.Empty : string.Empty,
                Provider = confirmationResult.TryGetValue("provider", out var provider)
                    ? provider?.ToString() ?? string.Empty : string.Empty,
                Summary = confirmationResult.TryGetValue("summary", out var summary)
                    ? summary?.ToString() ?? "Confirmed" : "Confirmed",
                Data = confirmationResult
            }
        };
    }

    // ==========================================================================================
    //  HELPERS
    // ==========================================================================================

    private enum ContinuationDecision { NewIntent, RefinePrevious }

    // ---- Conversational (meta / small-talk) classification ----

    private enum ConversationalIntent
    {
        None,
        AddressQuery,
        PreferencesQuery,
        SessionRecall,
        Help,
        Greeting,
        Thanks
    }

    /// <summary>
    /// Decide whether a prompt is a conversational / meta question that we can answer from
    /// session + user context (no agent fan-out) or a task that needs the discover pipeline.
    /// Bias: only return non-None when we're confident — misclassifying a task as chit-chat
    /// silently drops the user's request, so any ambiguity falls through to the normal flow.
    /// </summary>
    private static ConversationalIntent ClassifyConversationalIntent(string prompt, SessionState? session)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return ConversationalIntent.None;
        var lower = prompt.Trim().ToLowerInvariant();

        // Long prompts are almost certainly instructions — never treat as chit-chat.
        if (lower.Length > 220) return ConversationalIntent.None;

        // A leading imperative verb ("book me a pizza") always wins over meta-question wording.
        foreach (var v in NewTaskLeadingVerbs)
            if (lower.StartsWith(v, StringComparison.Ordinal))
                return ConversationalIntent.None;

        // Very short greetings.
        if (lower.Length <= 25 && Regex.IsMatch(lower, @"^(hi|hello|hey|yo|hola|greetings)\b[!\.\?]*$"))
            return ConversationalIntent.Greeting;

        // Thanks / acknowledgements.
        if (Regex.IsMatch(lower, @"^(thanks|thank you|ty|thx|cheers|appreciate it)\b[!\.\?]*$"))
            return ConversationalIntent.Thanks;

        // Address queries — user is asking about the address on file / session, not requesting
        // delivery. Guard against the food-delivery keyword "delivery" being mistaken for a
        // task by requiring an interrogative structure ("what/where/which/tell me/show me").
        if (Regex.IsMatch(lower, @"\b(what'?s?|which|where|tell me|show me|remind me)\b.{0,40}?\baddress\b")
            || Regex.IsMatch(lower, @"\b(what'?s?|which|where)\b.{0,40}?\b(deliver(y)? to|deliver(ing)? to)\b")
            || lower == "my address"
            || lower.StartsWith("my address ", StringComparison.Ordinal))
            return ConversationalIntent.AddressQuery;

        // Preferences queries.
        if (Regex.IsMatch(lower, @"\b(what'?s?|which)\b.{0,30}?\b(my )?(preferences?|profile|settings?)\b"))
            return ConversationalIntent.PreferencesQuery;

        // Session recall — only when there are prior options to recap.
        // Guard: reject when the prompt itself names a NEW task domain distinct from the
        // session's stored domain (e.g. previous session was food-delivery and the user
        // now asks "what are laptop options available to me?"). Without this, the recall
        // regex would greedily match "what … options" and silently reply with the old
        // food cards instead of fanning out to laptop agents.
        if (session is { Options.Count: > 0 } &&
            (Regex.IsMatch(lower, @"\b(what|which)\b.{0,30}?\b(options?|choices?|pizzas?|hotels?|flights?|results?|cards?|offers?)\b")
             || lower.StartsWith("recap", StringComparison.Ordinal)
             || lower.StartsWith("remind me", StringComparison.Ordinal)
             || lower.StartsWith("summari", StringComparison.Ordinal)  // summarize / summarise
             || lower.StartsWith("show them again", StringComparison.Ordinal)
             || lower.StartsWith("what did you show", StringComparison.Ordinal)))
        {
            var promptDomain = ExtractDomain(lower);
            var sessionDomain = session.PromptDomain ?? string.Empty;
            var domainConflicts =
                !string.IsNullOrEmpty(promptDomain)
                && !string.Equals(promptDomain, "general", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(promptDomain, sessionDomain, StringComparison.OrdinalIgnoreCase);

            if (!domainConflicts)
                return ConversationalIntent.SessionRecall;
        }

        // Help / capabilities.
        if (Regex.IsMatch(lower, @"^(help|who are you|what are you|what can you do)\b[!\.\?]*$")
            || Regex.IsMatch(lower, @"\b(how|what)\b.{0,20}\b(can|do) you (do|help)\b"))
            return ConversationalIntent.Help;

        return ConversationalIntent.None;
    }

    /// <summary>
    /// Build the assistant reply for a conversational intent. Pulls from session state and the
    /// user's saved profile — never invokes an agent.
    /// </summary>
    private async Task<string> BuildConversationalReplyAsync(
        ConversationalIntent intent, SessionState? session, UserContext userContext, CancellationToken ct)
    {
        switch (intent)
        {
            case ConversationalIntent.Greeting:
                return "Hi! I can help you book flights or hotels, order food, and more. What would you like to do?";

            case ConversationalIntent.Thanks:
                return "You're welcome. Let me know if there's anything else.";

            case ConversationalIntent.Help:
                return "I can help you:\n" +
                       "• Book flights and hotels\n" +
                       "• Order food for delivery\n" +
                       "• Reserve restaurants\n" +
                       "• Arrange package pickup\n\n" +
                       "Just tell me what you'd like — for example, \"book me a flight to Delhi next Friday\" or \"order me a pizza\".";

            case ConversationalIntent.AddressQuery:
            {
                var addr = session?.DeliveryAddress;
                if (string.IsNullOrWhiteSpace(addr))
                    addr = await _userContextService.GetSavedAddressAsync(userContext.UserId, ct);

                if (string.IsNullOrWhiteSpace(addr))
                    return "I don't have a delivery address on file for you yet. When you next place a delivery order I'll ask for one and remember it.";

                var confirmed = session?.AddressConfirmed == true;
                return confirmed
                    ? $"Your delivery address on file is:\n\n  {addr}"
                    : $"The most recent delivery address I have is:\n\n  {addr}\n\n(You haven't confirmed it for this session yet — I'll ask you to confirm it before placing any order.)";
            }

            case ConversationalIntent.PreferencesQuery:
            {
                if (userContext.Preferences.Count == 0)
                    return "I don't have any preferences saved for you yet.";
                var lines = userContext.Preferences.Select(p => $"• {p.Key}: {p.Value}");
                return "Here's what I have on file:\n\n" + string.Join("\n", lines);
            }

            case ConversationalIntent.SessionRecall:
            {
                if (session is null || session.Options.Count == 0)
                    return "I don't have any options from an earlier turn in this session to recap.";
                var lines = session.Options
                    .Select((o, i) =>
                    {
                        var summary = CollapseInline(o.Summary, 160);
                        return $"{i + 1}. {o.AgentName} — {summary}";
                    });
                return "Here are the options I shared earlier:\n\n" + string.Join("\n", lines) +
                       "\n\nSay the number or the provider name to pick one, or ask me to refine them.";
            }

            default:
                return "I'm here to help. What would you like to do?";
        }
    }

    /// <summary>
    /// Decide whether the new prompt is a refinement of the previous turn or a brand-new
    /// intent. Same domain → refinement; different domain but explicit refinement signals
    /// ("actually", "instead", short "make it …") → refinement; otherwise → new intent.
    /// </summary>
    private static ContinuationDecision ClassifyContinuation(
        string newPrompt, string newDomain, SessionState? previous)
    {
        if (previous is null || previous.Options.Count == 0)
            return ContinuationDecision.NewIntent;

        if (!string.IsNullOrEmpty(previous.PromptDomain)
            && string.Equals(previous.PromptDomain, newDomain, StringComparison.OrdinalIgnoreCase))
        {
            return ContinuationDecision.RefinePrevious;
        }

        if (HasRefinementSignal(newPrompt))
            return ContinuationDecision.RefinePrevious;

        return ContinuationDecision.NewIntent;
    }

    private static readonly string[] RefinementSignals =
    {
        "actually", "instead", "make it", "make that", "change to", "swap to", "switch to",
        "prefer", "cheaper", "pricier", "faster", "sooner", "later", "different", "another",
        "not that", "no,", "wrong", "same but", "similar but", "window seat", "aisle seat"
    };

    private static bool HasRefinementSignal(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var lower = prompt.ToLowerInvariant();
        // Long paragraphs are more likely a new intent than a follow-up.
        if (lower.Length > 240) return false;
        foreach (var s in RefinementSignals)
            if (lower.Contains(s, StringComparison.Ordinal)) return true;
        return false;
    }

    // ---- Delivery address slot ----

    private static readonly string[] DeliveryTriggers =
    {
        "deliver", "delivery", "pickup", "pick up", "order food", "food delivery",
        "courier", "parcel", "fedex", "dhl", "usps", "grocery",
        "to my place", "to my home", "to my address", "send to me"
    };

    /// <summary>
    /// Does this prompt / domain require a physical delivery or pickup address? True for the
    /// coarse categories the orchestrator knows about (food-delivery + package flows) and for
    /// explicit "deliver to" style phrases in any domain.
    /// </summary>
    private static bool RequiresDeliveryAddress(string prompt, string domain)
    {
        if (string.Equals(domain, "food-delivery", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(domain, "package-pickup", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var lower = prompt.ToLowerInvariant();
        foreach (var t in DeliveryTriggers)
            if (lower.Contains(t, StringComparison.Ordinal)) return true;
        return false;
    }

    private static readonly Regex AddressRegex = new(
        @"(\b\d{1,6}\s+\w[\w.\s-]{2,}?(street|st|avenue|ave|road|rd|boulevard|blvd|lane|ln|drive|dr|way|court|ct|place|pl|highway|hwy)\b)"
        + @"|(\b\d{5}(-\d{4})?\b)"                       // US ZIP
        + @"|(\b[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}\b)",   // UK postcode
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool LooksLikeAddress(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var trimmed = s.Trim();
        if (trimmed.Length < 6 || trimmed.Length > 300) return false;
        if (AddressRegex.IsMatch(trimmed)) return true;
        // Fallback: starts with a number + a word ("221B Baker Street, London").
        return Regex.IsMatch(trimmed, @"^\d+[A-Za-z]?\s+\w+");
    }

    private static readonly HashSet<string> Affirmations = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "y", "yep", "yeah", "yup", "sure", "ok", "okay", "confirm", "confirmed",
        "use it", "use this", "go ahead", "proceed", "sounds good", "correct", "that's right"
    };

    private static bool LooksLikeAffirmation(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var trimmed = s.Trim().TrimEnd('.', '!').ToLowerInvariant();
        return Affirmations.Contains(trimmed) || trimmed.StartsWith("yes ", StringComparison.Ordinal);
    }

    private static readonly HashSet<string> Cancellations = new(StringComparer.OrdinalIgnoreCase)
    {
        "cancel", "cancel it", "nevermind", "never mind", "stop", "abort",
        "forget it", "forget that", "skip", "skip it", "no thanks", "no, thanks",
        "not now", "not anymore", "drop it"
    };

    private static bool LooksLikeCancellation(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var trimmed = s.Trim().TrimEnd('.', '!').ToLowerInvariant();
        return Cancellations.Contains(trimmed);
    }

    // Strong "start a new task" verbs and phrases. When the address slot is open, seeing any
    // of these at the start of the prompt is a clear signal the user has moved on rather than
    // giving us an address. This deliberately errs on the side of NOT abandoning — a plain
    // address like "MG Road, Bengaluru" contains none of these tokens and will be captured.
    private static readonly string[] NewTaskLeadingVerbs =
    {
        "book ", "order ", "reserve ", "reservations ", "find ", "search ", "look for ",
        "show me ", "get me ", "plan ", "schedule ", "buy ", "arrange ", "help me ",
        "i want ", "i'd like ", "i would like ", "can you ", "could you "
    };

    private static bool LooksLikeNewTask(string prompt, string parkedDomain)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var trimmed = prompt.Trim();
        var lower = trimmed.ToLowerInvariant();

        // Signal 1: leading imperative verb.
        foreach (var v in NewTaskLeadingVerbs)
            if (lower.StartsWith(v, StringComparison.Ordinal))
                return true;

        // Signal 2: recognizable domain classification DIFFERENT from the parked one, and long
        // enough to be a task (not just a one-word answer). E.g. "book a flight to LA" while
        // the parked prompt was food-delivery.
        if (trimmed.Length >= 12)
        {
            var newDomain = ExtractDomain(lower);
            if (!string.IsNullOrEmpty(newDomain)
                && !string.Equals(newDomain, "general", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(newDomain, parkedDomain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // ---- Execute guardrails ----

    private sealed record GuardrailDecision(bool RequiresConfirmation, string Reason);

    /// <summary>
    /// Decide whether Execute may auto-complete the action or must route the final commit
    /// through /confirm. Bias: default-deny — anything with a price, physical fulfillment,
    /// or an unclassified domain routes to confirm. Only actions we've explicitly deemed
    /// safe (currently none — all supported domains are user-critical) can auto-complete.
    /// </summary>
    private static GuardrailDecision EvaluateExecuteGuardrails(SessionState? session)
    {
        var domain = session?.PromptDomain ?? string.Empty;

        if (domain is "flights" or "hotels" or "food-delivery" or "dining" or "package-pickup")
        {
            return new GuardrailDecision(
                RequiresConfirmation: true,
                Reason: $"'{domain}' actions charge money or affect a real reservation.");
        }

        // Default-deny for unknown domains.
        return new GuardrailDecision(
            RequiresConfirmation: true,
            Reason: "the action isn't classified as safe to auto-commit.");
    }

    private static string ComposeExecutePrompt(
        string userPrompt,
        SessionOption? selected,
        GuardrailDecision guardrail,
        string promptDomain,
        string? deliveryAddress,
        IReadOnlyDictionary<string, string> userPreferences)
    {
        var body = string.IsNullOrWhiteSpace(userPrompt)
            ? "Proceed with the selected option. Assume sensible defaults; do not ask questions."
            : userPrompt;

        // Guardrails are duplicated on every Execute — never trust the agent to remember them
        // across turns. Kept short so the agent still has room to produce a preview.
        var guardBlock =
            "STRICT GUARDRAILS — you MUST obey these before doing anything else:\n" +
            "1. DO NOT complete any irreversible action (no payment, no ticketing, no order " +
            "placement, no reservation commit, no email/SMS to the user or any third party).\n" +
            "2. Prepare the action as far as safely possible without a commit. Confirm holds " +
            "and previews are OK; anything that spends money or creates a record of intent to " +
            "purchase is NOT.\n" +
            "3. Do not invent identifiers (confirmation numbers, ticket numbers) — leave those " +
            "for the confirm step.";

        var selectionBlock = selected is null
            ? string.Empty
            : $"\n\nSELECTED OPTION: {selected.AgentName} · {selected.Summary}";

        var reasonBlock = guardrail.RequiresConfirmation
            ? $"\n\nCONFIRM-REQUIRED: {guardrail.Reason}"
            : string.Empty;

        // Real user context — the client card renders these fields directly, so if the agent
        // makes them up (or leaves them blank) the user sees dummy data. Explicitly hand them
        // through and require the agent to echo them back verbatim in the JSON preview.
        var contextLines = new List<string>();
        if (!string.IsNullOrWhiteSpace(deliveryAddress))
            contextLines.Add($"- delivery_address: {deliveryAddress}");
        if (userPreferences.TryGetValue("payment_method", out var pm) && !string.IsNullOrWhiteSpace(pm))
            contextLines.Add($"- payment_method: {pm}");
        if (userPreferences.TryGetValue("name", out var nm) && !string.IsNullOrWhiteSpace(nm))
            contextLines.Add($"- customer_name: {nm}");
        if (userPreferences.TryGetValue("phone", out var ph) && !string.IsNullOrWhiteSpace(ph))
            contextLines.Add($"- phone: {ph}");
        var contextBlock = contextLines.Count > 0
            ? "\n\nUSER CONTEXT (use these values verbatim in your preview — do NOT invent addresses, cards, or contact details):\n"
              + string.Join("\n", contextLines)
            : string.Empty;

        // Domain-specific preview fields — hint what a good preview looks like for this flow.
        // The agent MUST return a JSON object (fenced in ```json``` if possible) with these keys
        // plus any additional keys under `details`.
        var previewFields = promptDomain switch
        {
            "flights" =>
                "title, description, price (number), currency, passenger_name, seat, cabin, " +
                "meal, departure, arrival, delivery_address (skip for flights), payment_method, details {baggage, refundable}",
            "hotels" =>
                "title, description, price (number), currency, guest_name, check_in, check_out, " +
                "room_type, delivery_address (skip), payment_method, details {breakfast, cancellation}",
            "food-delivery" =>
                "title, description, price (number), currency, delivery_address, payment_method, " +
                "eta, items (array of {name, qty, notes}), details {toppings, size, drinks}",
            "dining" =>
                "title, description, price (number), currency, guest_name, party_size, reservation_time, " +
                "delivery_address (skip), payment_method, details {seating_preference, occasion}",
            _ =>
                "title, description, price (number), currency, delivery_address, payment_method, details"
        };

        var outputBlock =
            "\n\nRESPONSE FORMAT (STRICT):\n" +
            "Return exactly ONE JSON object wrapped in ```json``` fences. The object MUST include: " +
            previewFields + ". " +
            "Use the USER CONTEXT values verbatim for delivery_address, payment_method, customer_name, phone. " +
            "For fields you genuinely don't have (e.g. seat, meal, toppings), pick sensible defaults and " +
            "note them concisely in `details`. Do NOT ask clarifying questions. Do NOT include prose " +
            "before or after the JSON block.";

        return $"{guardBlock}{selectionBlock}{reasonBlock}{contextBlock}\n\nUSER REQUEST: {body}{outputBlock}";
    }

    /// <summary>
    /// Parse the agent's Execute response into a flat dictionary the client can render on the
    /// confirmation card. Accepts JSON wrapped in ```json``` fences, a raw JSON object, or a
    /// JSON array (first element wins). Returns an empty dictionary when nothing parses — the
    /// caller then falls back to the raw summary string.
    /// </summary>
    private static Dictionary<string, object> ParseExecutePreview(string summary)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(summary)) return result;

        var jsonSpan = FindFirstJsonObjectSpan(summary);
        if (string.IsNullOrEmpty(jsonSpan)) return result;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(jsonSpan);
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array && root.GetArrayLength() > 0)
                root = root[0];
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return result;

            foreach (var prop in root.EnumerateObject())
            {
                switch (prop.Value.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.String:
                        result[prop.Name] = prop.Value.GetString() ?? string.Empty;
                        break;
                    case System.Text.Json.JsonValueKind.Number:
                        if (prop.Value.TryGetDecimal(out var d)) result[prop.Name] = d;
                        else result[prop.Name] = prop.Value.ToString();
                        break;
                    case System.Text.Json.JsonValueKind.True:
                    case System.Text.Json.JsonValueKind.False:
                        result[prop.Name] = prop.Value.GetBoolean();
                        break;
                    case System.Text.Json.JsonValueKind.Object:
                    case System.Text.Json.JsonValueKind.Array:
                        // Keep nested structures as raw JSON so the client can inspect them.
                        result[prop.Name] = prop.Value.GetRawText();
                        break;
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Non-JSON summary — leave the raw text on Confirmation.Summary and return empty.
        }

        return result;
    }

    /// <summary>Return the first balanced-brace JSON object span in <paramref name="text"/>, honoring ```json``` fences first.</summary>
    private static string? FindFirstJsonObjectSpan(string text)
    {
        // 1. Prefer explicit ```json fenced blocks.
        var fenceIdx = text.IndexOf("```", StringComparison.Ordinal);
        if (fenceIdx >= 0)
        {
            var afterFence = fenceIdx + 3;
            var lineEnd = text.IndexOf('\n', afterFence);
            if (lineEnd > 0)
            {
                var close = text.IndexOf("```", lineEnd + 1, StringComparison.Ordinal);
                if (close > 0)
                {
                    var fenced = text[(lineEnd + 1)..close].Trim();
                    if (fenced.Length > 0 && (fenced[0] == '{' || fenced[0] == '['))
                        return fenced;
                }
            }
        }

        // 2. Fall back to the first balanced object.
        for (var start = 0; start < text.Length; start++)
        {
            if (text[start] != '{') continue;
            var depth = 0;
            for (var i = start; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return text.Substring(start, i - start + 1);
                }
            }
        }
        return null;
    }

    // ---- Prompt assembly ----

    private static string BuildEnrichedPrompt(
        string prompt,
        UserContext userContext,
        List<UserEmbeddingDocument> contextDocs,
        string? deliveryAddress = null,
        string? refinementOf = null,
        IReadOnlyList<string>? preferredBrands = null,
        IReadOnlyList<string>? previouslyShownSummaries = null)
    {
        var contextParts = new List<string> { prompt };

        if (!string.IsNullOrWhiteSpace(refinementOf))
        {
            contextParts.Add(
                "This is a REFINEMENT of the user's previous request. Original request: \"" +
                refinementOf.Trim() +
                "\". Treat the new prompt as a modification of the earlier request and keep any " +
                "unchanged constraints from the original.");
        }

        if (preferredBrands is { Count: > 0 })
        {
            contextParts.Add(
                "BRAND CONSTRAINT (hard): the user explicitly asked for options from " +
                string.Join(" / ", preferredBrands) + ". Return offers from this provider ONLY. " +
                "If you cannot produce a matching offer, return an empty JSON array [] — do NOT " +
                "substitute with a different provider.");
        }

        if (previouslyShownSummaries is { Count: > 0 })
        {
            // Cap to keep tokens reasonable and avoid dumping raw JSON back into the prompt.
            var trimmed = previouslyShownSummaries
                .Take(6)
                .Select(s => "- " + CollapseInline(s, 200));
            contextParts.Add(
                "Options already shown to the user (do NOT repeat these — produce DIFFERENT " +
                "offers, e.g. different times, routes, cabins, or price points):\n" +
                string.Join("\n", trimmed));
        }

        if (!string.IsNullOrWhiteSpace(deliveryAddress))
        {
            contextParts.Add("Deliver to (already confirmed by user): " + deliveryAddress.Trim());
        }

        if (userContext.Preferences.Count > 0)
        {
            contextParts.Add("User Preferences: " +
                string.Join("; ", userContext.Preferences.Select(p => $"{p.Key}: {p.Value}")));
        }

        if (contextDocs.Count > 0)
        {
            contextParts.Add("Relevant History: " +
                string.Join("; ", contextDocs.Select(d => d.Content)));
        }

        // Force compact, structured JSON output and forbid clarifications.
        contextParts.Add(
            "IMPORTANT RESPONSE RULES (follow strictly):\n" +
            "1. DO NOT ask any clarifying questions. Do not ask the user to confirm dates, " +
            "passenger counts, cabin class, quantities, sizes, or any other missing detail.\n" +
            "2. If information is missing, assume sensible defaults and note them concisely " +
            "inside the description (e.g. \"1 adult, Economy, one-way\").\n" +
            "3. Use today's date (" + DateTime.UtcNow.ToString("yyyy-MM-dd") + ") to resolve " +
            "relative dates like \"next Friday\" or \"tomorrow\".\n" +
            "4. Return 2-3 concrete offers as a JSON array wrapped in ```json``` fences. Each " +
            "item MUST include: title, description, price (number), currency.\n" +
            "   • If the request is squarely in YOUR domain (e.g. you are a laptop brand agent " +
            "and the user asks for a laptop), you MUST return 2-3 specific offers drawn from " +
            "your own brand's realistic product catalog — even if you don't have a live " +
            "inventory feed. Use plausible current-generation SKUs, models, and prices from " +
            "your brand. Do NOT return an empty array just because you lack a database.\n" +
            "   • Return an empty JSON array [] ONLY when the request is CLEARLY OUTSIDE your " +
            "domain (e.g. a pizza brand asked for a laptop, an airline asked for hotels). Never " +
            "invent items outside your domain to fit the prompt.\n" +
            "5. 'title' MUST be a short, specific identifier of THIS offer — the concrete item / " +
            "booking / reservation reference the user is choosing between. Examples: " +
            "\"Margherita Pizza (12in)\", \"Legion Slim 5 · RTX 4060 · 16GB\", " +
            "\"Flight AI102 · JFK→LHR\", \"Reservation #R-8821\". NEVER a generic label like " +
            "\"Option 1\", \"Pizza\", \"Laptop\", or the provider name alone.\n" +
            "6. 'description' MUST be 20–45 words explaining what the offer includes, why it fits " +
            "the user's request, and any assumed defaults. Include the decision-worthy detail the " +
            "user needs to pick between offers (specs, times, toppings, room type, etc.). No " +
            "marketing prose, no emojis, no restating the title verbatim.\n" +
            "7. Include ONLY the attributes that materially help the user pick (departure, arrival, " +
            "duration, stops, cabin, eta, delivery_eta, cuisine, rating, distance). Do not dump " +
            "every field the provider returned.\n" +
            "8. Do not add prose before or after the JSON array — the JSON block is the entire response.");

        return string.Join("\n\n", contextParts);
    }

    // ---- Brand preference extraction ----

    // Words that appear inside agent names but shouldn't be treated as brand tokens. A brand
    // token has to actually identify a *provider* (Dell, Emirates, Dominos) — generic product
    // categories (laptop, pizza, flight) and role words (agent, service) are not brands, and
    // treating them as such causes silent fan-out narrowing. The user's real report was that
    // "Find me a gaming laptop under $1,000" matched only 'Dell Laptop Agent' and dropped
    // 'Lenovo-Accessories-Agent' because the prompt's word "laptop" appeared in Dell's name
    // and not in Lenovo's — even though "laptop" is not a brand.
    private static readonly HashSet<string> BrandStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Role / infrastructure words
        "agent", "bot", "assistant", "helper", "service", "provider", "prod", "dev", "test",
        "the", "and", "for", "of", "on", "in", "app", "api", "v1", "v2", "v3",

        // Travel / food generic nouns
        "flight", "flights", "hotel", "hotels", "food", "delivery", "dining", "pickup", "package",
        "restaurant", "restaurants", "airline", "airlines", "airfare", "trip", "travel",
        "booking", "reservation", "reservations", "stay", "meal", "meals",

        // Food items (generic — brands are Dominos / PizzaHut / McDonalds, etc.)
        "pizza", "pizzas", "burger", "burgers", "sushi", "biryani", "noodles", "pasta",
        "sandwich", "cuisine", "dessert", "salad", "ramen", "curry", "taco", "tacos",
        "sub", "wrap", "wings", "breakfast", "lunch", "dinner", "brunch", "coffee", "tea",

        // Electronics / laptop generic nouns (this is the one that caused the reported bug)
        "laptop", "laptops", "notebook", "notebooks", "computer", "computers", "desktop", "desktops",
        "ultrabook", "chromebook", "tablet", "tablets", "smartphone", "phone", "phones",
        "monitor", "monitors", "keyboard", "keyboards", "mouse", "mice", "headphone", "headphones",
        "earbud", "earbuds", "speaker", "speakers", "camera", "cameras", "console", "consoles",
        "accessory", "accessories", "electronics", "gaming", "gamer", "gadget", "gadgets",
        "hardware", "peripheral", "peripherals",

        // Generic spec / adjective tokens sometimes baked into agent names
        "budget", "premium", "basic", "standard", "pro", "plus", "max", "mini", "lite",
        "store", "shop", "market", "hub", "portal", "online"
    };

    /// <summary>
    /// Given the refinement prompt and the pool of agents available on this session, return
    /// the brand tokens the user explicitly named (e.g. "emirates", "united", "dominos").
    /// A brand is any token that appears in the prompt AND matches at least one agent's name.
    ///
    /// Matching runs on two flavours of the prompt so we cover the common ways brand names
    /// get typed:
    ///   1. Word-tokenized set — matches when the prompt uses the same concatenation as the
    ///      agent name ("airindia" ↔ "AirIndia-flight-agent").
    ///   2. Compact form (letters/digits only, spaces removed) — matches when the user typed
    ///      the brand with a space that the agent name doesn't have ("Air India" → "airindia").
    /// </summary>
    private static List<string> ExtractBrandPreferences(string prompt, IEnumerable<AgentInfo> availableAgents)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return [];
        var promptLower = prompt.ToLowerInvariant();
        var promptTokens = TokenizeAlpha(promptLower).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var promptCompact = new string(promptLower.Where(char.IsLetterOrDigit).ToArray());
        if (promptTokens.Count == 0 && promptCompact.Length == 0) return [];

        var matched = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in availableAgents)
        {
            foreach (var brandTok in BrandTokens(agent.Name).Concat(BrandTokens(agent.AgentId)))
            {
                var hit = promptTokens.Contains(brandTok)
                    || (brandTok.Length >= 4 && promptCompact.Contains(brandTok, StringComparison.Ordinal));
                if (hit && seen.Add(brandTok))
                    matched.Add(brandTok);
            }
        }
        return matched;
    }

    /// <summary>True if the agent's name or id contains any of the requested brand tokens.</summary>
    private static bool AgentMatchesAnyBrand(string agentName, string agentId, IEnumerable<string> brands)
    {
        var haystack = ((agentName ?? string.Empty) + " " + (agentId ?? string.Empty)).ToLowerInvariant();
        foreach (var b in brands)
            if (!string.IsNullOrEmpty(b) && haystack.Contains(b.ToLowerInvariant(), StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>Split a name/id into candidate brand tokens, filtering out generic stopwords.</summary>
    private static IEnumerable<string> BrandTokens(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) yield break;
        foreach (var tok in TokenizeAlpha(source))
        {
            if (tok.Length < 3) continue;
            if (BrandStopWords.Contains(tok)) continue;
            yield return tok;
        }
    }

    private static IEnumerable<string> TokenizeAlpha(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var sb = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetter(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string CollapseInline(string value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var s = Regex.Replace(value, @"\s+", " ").Trim();
        return s.Length <= maxLen ? s : s[..maxLen] + "…";
    }

    private static string ExtractDomain(string prompt)
    {
        // Keep aligned with SkillExecutor.ClassifyDomain so promptDomain -> AgentType lookups match.
        var promptLower = prompt.ToLowerInvariant();
        if (promptLower.Contains("flight") || promptLower.Contains("fly") || promptLower.Contains("airline")
            || promptLower.Contains("emirates") || promptLower.Contains("airindia"))
            return "flights";
        if (promptLower.Contains("hotel") || promptLower.Contains("stay") || promptLower.Contains("accommodation"))
            return "hotels";
        if (promptLower.Contains("pizza") || promptLower.Contains("burger") || promptLower.Contains("order food")
            || promptLower.Contains("delivery") || promptLower.Contains("takeout")
            || promptLower.Contains("dominos") || promptLower.Contains("ubereats") || promptLower.Contains("swiggy"))
            return "food-delivery";
        if (promptLower.Contains("restaurant") || promptLower.Contains("dining") || promptLower.Contains("reservation"))
            return "dining";
        if (promptLower.Contains("pickup") || promptLower.Contains("pick up") || promptLower.Contains("parcel")
            || promptLower.Contains("courier") || promptLower.Contains("fedex") || promptLower.Contains("dhl")
            || promptLower.Contains("usps"))
            return "package-pickup";
        if (promptLower.Contains("laptop") || promptLower.Contains("notebook") || promptLower.Contains("macbook")
            || promptLower.Contains("computer") || promptLower.Contains("desktop")
            || promptLower.Contains("smartphone") || promptLower.Contains("iphone")
            || promptLower.Contains("headphone") || promptLower.Contains("headphones")
            || promptLower.Contains("monitor") || promptLower.Contains("keyboard")
            || promptLower.Contains("gaming") || promptLower.Contains("rtx") || promptLower.Contains("gpu")
            || promptLower.Contains("playstation") || promptLower.Contains("xbox") || promptLower.Contains("nintendo"))
            return "electronics";
        return "general";
    }

    /// <summary>
    /// Turn an internal domain slug ("food-delivery", "flights") into a human-readable label
    /// used in user-facing "no relevant agent" messages.
    /// </summary>
    private static string FormatDomainLabel(string domain) => domain switch
    {
        "flights" => "flight",
        "hotels" => "hotel",
        "food-delivery" => "food-delivery",
        "dining" => "restaurant",
        "package-pickup" => "package-pickup",
        "electronics" => "electronics",
        "" or "general" => "general",
        _ => domain
    };
}
