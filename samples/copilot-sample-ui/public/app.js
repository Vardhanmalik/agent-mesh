/* ==========================================================================
 * Copilot Agent Mesh — Sample UI client
 *
 * Talks to the real OrchestratorEngine.Api:
 *   - GET  /api/orchestrator/health           (sidebar dot)
 *   - POST /api/orchestrator/discover         (user prompt)
 *   - POST /api/orchestrator/execute          (Buy / Order click)
 *   - POST /api/orchestrator/confirm          (Pay / Confirm click)
 *
 * Renders the /discover response in the Agent Mesh look:
 *   - Assistant "analysis" bubble (from response.message)
 *   - One card per agent (options grouped by agent_id)
 *   - Each option becomes a pick row with heuristic thumbnail, price, and Buy/Order.
 *
 * Suggestion cards on the welcome screen just seed the composer with a starter
 * prompt (laptop / pizza) — they do not fake results.
 * ========================================================================== */

(() => {
    'use strict';

    // ==========================================================================
    // Configuration
    // ==========================================================================

    /** Item image pools keyed by category. Any random image from the matching
     *  category is attached to an option row when the user's prompt implies
     *  laptops, flights, or food. Anything else leaves rows imageless. */
    const ITEM_IMAGES = {
        laptop: [
            '/assets/items/laptop/dell-2.jpg',
            '/assets/items/laptop/laptop-1.jpg',
            '/assets/items/laptop/laptop-3.jpg',
            '/assets/items/laptop/laptop-4.jpg',
            '/assets/items/laptop/laptopn-2.png'
        ],
        flight: [
            '/assets/items/flight/flight.jpg',
            '/assets/items/flight/flight-2.jpg',
            '/assets/items/flight/flight-3.jpg',
            '/assets/items/flight/flight-4.jpg'
        ],
        food: [
            '/assets/items/food/burger-1.jpg',
            '/assets/items/food/pasta-2.jpg',
            '/assets/items/food/pizza-1.jpg',
            '/assets/items/food/pizza-2.jpg',
            '/assets/items/food/pizza-3.jpg'
        ]
    };

    /** Labels cycled through by the pay/confirm loader. */
    const CONFIRM_LOADER_STEPS = [
        'Matching identity…',
        'Matching identity, payment…',
        'Matching identity, payment, address…'
    ];

    const AGENT_TONES = ['brand', 'success', 'warning'];

    /** Sample follow-up prompts shown in the "chat with agent" banner. Keyed by the
     *  imageCategory the prompt fell into so the chips feel contextual. */
    const SOLO_SAMPLE_PROMPTS = {
        laptop: ['Which is best for 4K video editing?', 'Is there a discount?'],
        flight: ['Any nonstop options?', "What's the cheapest fare?"],
        food:   ["What are today's specials?", 'Any vegetarian options?'],
        default: ["What's your top recommendation?", 'Any current offers?']
    };

    /**
     * NOTE: Static product photography and brand logos used to live here. They've been
     * removed — every card now renders solely from what the hosted agent actually
     * returns (title / description / attributes), with initials-based avatars as the
     * only visual fallback. If you need imagery back, have the agent supply URLs
     * in the option payload instead of adding client-side keyword tables.
     */

    // ==========================================================================
    // Utilities
    // ==========================================================================

    const $ = (id) => document.getElementById(id);
    const makeId = () => `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    const makeOrderNumber = () => `AM-${Math.floor(10000 + Math.random() * 90000)}`;

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined && text !== null) node.textContent = text;
        return node;
    }

    function initials(name) {
        return (name || '')
            .split(/[\s\-_.]+/)
            .filter(Boolean)
            .slice(0, 2)
            .map((s) => s[0].toUpperCase())
            .join('') || 'A';
    }

    function firstMatch(list, haystack) {
        const h = (haystack || '').toLowerCase();
        for (const entry of list) if (h.includes(entry.kw)) return entry.src;
        return null;
    }

    /** Split "Legion Slim 5 — RTX 4060, Ryzen 7, 16GB, 144Hz — ships in 2 days" into a
     *  short title + a detail string. If no natural split, fall back to a truncated title. */
    function splitDetails(details) {
        const raw = (details || '').trim();
        if (!raw) return { title: 'Option', rest: '' };
        // Try common separators: em dash / en dash / hyphen with spaces / period.
        const seps = [' — ', ' – ', ' - ', '. ', ': ', ' – ', '\n'];
        for (const sep of seps) {
            const i = raw.indexOf(sep);
            if (i > 0 && i < 80) {
                return { title: raw.slice(0, i).trim(), rest: raw.slice(i + sep.length).trim() };
            }
        }
        // Fallback: first sentence or 60 chars.
        const cap = raw.length > 70 ? raw.slice(0, 70).trim() + '…' : raw;
        return { title: cap, rest: raw.length > 70 ? raw : '' };
    }

    /** Preferred title for an option — server-provided `title` takes precedence over the
     *  heuristic split of `details`. */
    function optionTitle(option) {
        return (option && typeof option.title === 'string' && option.title.trim())
            || splitDetails(option && option.details).title;
    }

    /** Preferred detail body for an option — server-provided `description` beats the
     *  heuristic split. */
    function optionDetail(option) {
        return (option && typeof option.description === 'string' && option.description.trim())
            || splitDetails(option && option.details).rest;
    }

    /** Try to make a nice short agent name (drop the trailing " Agent" that many
     *  backends append). Preserve the original for the avatar's alt. */
    function displayAgentName(agentName) {
        return (agentName || 'Agent').replace(/\s+agent$/i, '').trim() || agentName || 'Agent';
    }

    function renderAvatar(agentId, agentName, tone, size /* 32|24|16 */) {
        // We no longer render brand logos here — every agent gets an initials-based
        // avatar so the visual is always sourced from the agent identity the hosted
        // orchestrator actually returned.
        const wrapper = el('span', `avatar tone-${tone} size-${size}`);
        wrapper.textContent = initials(agentName);
        return wrapper;
    }

    // ==========================================================================
    // Icons (inline SVG helpers)
    // ==========================================================================

    const ICON_CHEVRON_RIGHT = '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="9 18 15 12 9 6"/></svg>';
    const ICON_LOCATION = '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 10c0 7-9 13-9 13S3 17 3 10a9 9 0 1 1 18 0z"/><circle cx="12" cy="10" r="3"/></svg>';
    const ICON_PAYMENT = '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="5" width="20" height="14" rx="2"/><path d="M2 10h20"/></svg>';
    const ICON_CHECK_FILLED = '<svg viewBox="0 0 24 24" width="24" height="24" fill="currentColor"><path d="M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2zm-1.4 14.4-4.2-4.2 1.4-1.4 2.8 2.8 5.6-5.6 1.4 1.4z"/></svg>';
    const ICON_CHECK_SMALL = '<svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor"><path d="M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2zm-1.4 14.4-4.2-4.2 1.4-1.4 2.8 2.8 5.6-5.6 1.4 1.4z"/></svg>';
    const ICON_CIRCLE_SMALL = '<svg viewBox="0 0 24 24" width="16" height="16" fill="currentColor"><circle cx="12" cy="12" r="6"/></svg>';
    const ICON_CLOSE = '<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M18 6 6 18M6 6l12 12"/></svg>';
    const ICON_CHAT_LIST = '<svg viewBox="0 0 24 24" class="chat-icon" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>';

    // ==========================================================================
    // State
    // ==========================================================================

    /**
     * @typedef {Object} Turn
     * @property {string} id
     * @property {'user'|'thinking'|'orchestration'|'purchase'|'assistant'|'error'} type
     */
    const state = {
        userId: 'Vardhan Malik',
        sessionId: null,
        /** @type {Turn[]} Ordered transcript. */
        turns: [],
        /** Purchase flow already in progress? Used to disable duplicate Buy clicks. */
        purchaseInFlight: false,
        /** Chats in the sidebar (first user turn becomes the label). */
        chats: [],
        activeChatId: null,
        /** Suggestion the user picked most recently — controls default Buy/Order label. */
        suggestionActionLabel: 'Buy',
        /** When set, only prompts routed to this agent are shown/sent. */
        soloAgent: null, // { agentId, agentName, imageCategory }
        toasts: []
    };

    // ==========================================================================
    // DOM refs
    // ==========================================================================

    const welcomeEl = $('welcomeScreen');
    const messagesEl = $('messages');
    const messagesInner = $('messagesInner');
    const composerEl = $('composer');
    const composerTagsEl = $('composerTags');
    const promptEl = $('promptInput');
    const sendBtn = $('sendBtn');
    const chatTitle = $('chatTitle');
    const chatSub = $('chatSub');
    const chatList = $('chatList');
    const userIdInput = $('userIdInput');
    const apiStatus = $('apiStatus');
    const newChatBtn = $('newChatBtn');
    const clearBtn = $('clearBtn');
    const suggestionsEl = null;
    const toastStackEl = $('toastStack');
    const sessionPill = $('sessionPill');
    const sessionIdText = $('sessionIdText');

    // ==========================================================================
    // API layer
    // ==========================================================================

    async function callApi(path, body) {
        const res = await fetch(path, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
            body: JSON.stringify(body)
        });
        const text = await res.text();
        let data = null;
        try { data = text ? JSON.parse(text) : null; } catch { /* keep raw */ }
        if (!res.ok) {
            const errMsg = (data && (data.error || data.detail || data.title || data.message)) || text || `HTTP ${res.status}`;
            throw new Error(errMsg);
        }
        return data;
    }

    async function checkHealth() {
        try {
            const res = await fetch('/api/orchestrator/health', { method: 'GET' });
            if (!res.ok) throw new Error('health check failed');
            apiStatus.classList.remove('err');
            apiStatus.classList.add('ok');
            apiStatus.querySelector('.status-text').textContent = 'API connected';
        } catch {
            apiStatus.classList.remove('ok');
            apiStatus.classList.add('err');
            apiStatus.querySelector('.status-text').textContent = 'API unreachable';
        }
    }

    // ==========================================================================
    // Turn helpers
    // ==========================================================================

    function pushTurn(turn) {
        state.turns.push(turn);
        render();
    }

    function updateTurn(id, patch) {
        state.turns = state.turns.map((t) => (t.id === id ? { ...t, ...patch } : t));
        render();
    }

    function removeTurn(id) {
        state.turns = state.turns.filter((t) => t.id !== id);
        render();
    }

    // ==========================================================================
    // Submit handler → real /discover call
    // ==========================================================================

    async function handleSubmit(promptText) {
        const text = (promptText || '').trim();
        if (!text) return;

        // Ensure we're out of the welcome state.
        if (welcomeEl && !welcomeEl.hidden) {
            welcomeEl.hidden = true;
            messagesEl.hidden = false;
        }

        // Seed / update the sidebar chat.
        upsertChatFromPrompt(text);

        state.suggestionActionLabel = pickActionLabelForPrompt(text);

        pushTurn({ id: makeId(), type: 'user', text });
        const thinkingId = makeId();
        pushTurn({
            id: thinkingId,
            type: 'thinking',
            label: 'Copilot is fanning out to your brand agents…'
        });

        try {
            const resp = await callApi('/api/orchestrator/discover', {
                userId: state.userId,
                prompt: text,
                sessionId: state.sessionId,
                // Solo mode ("Chat with agent"): sent as a first-class field so the backend
                // scopes fan-out to this agent alone. The client also filters below in case
                // the backend didn't honor the hint.
                ...(state.soloAgent ? { preferredAgentId: state.soloAgent.agentId } : {}),
                context: {
                    source: 'copilot-agent-mesh-sample'
                }
            });
            if (resp && resp.sessionId) {
                state.sessionId = resp.sessionId;
                sessionPill.hidden = false;
                sessionIdText.textContent = resp.sessionId;
            }
            removeTurn(thinkingId);

            const status = (resp && resp.status) || '';
            const message = (resp && resp.message) || '';
            let options = Array.isArray(resp && resp.options) ? resp.options : [];

            if (status === 'Informational' || (status === 'NeedsAddress' && !options.length)) {
                pushTurn({ id: makeId(), type: 'assistant', text: message || 'Okay.' });
                return;
            }

            // In solo mode, keep only the active agent's options — this is what makes
            // the follow-up prompt behave as if it were routed to that agent alone.
            if (state.soloAgent) {
                options = options.filter((o) =>
                    (o.agent_id && o.agent_id === state.soloAgent.agentId)
                    || (o.agent_name && o.agent_name === state.soloAgent.agentName));
            }

            if (!options.length) {
                pushTurn({
                    id: makeId(),
                    type: 'assistant',
                    text: message
                        || (state.soloAgent
                            ? `${displayAgentName(state.soloAgent.agentName)} didn't have anything for that. Try refining the prompt.`
                            : 'No agents returned options for that request. Try refining the prompt.')
                });
                return;
            }
            const agents = groupOptionsByAgent(options);
            // Image category: the current prompt wins. Solo mode only supplies a
            // fallback when the prompt itself doesn't disambiguate (e.g. "any deals?"
            // while chatting with a laptop agent). Without this, chatting with a
            // laptop agent and asking for "burger options" would render laptop
            // thumbnails on the burger cards.
            const promptImageCategory = pickImageCategoryForPrompt(text);
            const orchestrationImageCategory = promptImageCategory
                || (state.soloAgent ? state.soloAgent.imageCategory : null);

            // Assign one image per option for THIS turn. We overwrite any prior
            // `_imgSrc` on the same option reference so a category change (laptop →
            // food) can't leave a stale thumbnail behind.
            agents.forEach((group) => {
                group.options.forEach((opt) => {
                    opt._imgSrc = orchestrationImageCategory
                        ? pickRandomImage(orchestrationImageCategory)
                        : null;
                });
            });

            pushTurn({
                id: makeId(),
                type: 'orchestration',
                message: composeDiscoverPreamble(message, agents, text),
                agents,
                actionLabel: state.suggestionActionLabel,
                imageCategory: orchestrationImageCategory
            });
        } catch (err) {
            removeTurn(thinkingId);
            pushTurn({ id: makeId(), type: 'error', text: err && err.message ? err.message : String(err) });
        }
    }

    function pickActionLabelForPrompt(text) {
        const t = (text || '').toLowerCase();
        if (/pizza|food|deliver|dinner|lunch|breakfast|meal|eat|parcel|pickup|pick up/.test(t)) return 'Order';
        if (/flight|hotel|book|reservation|stay|trip|travel/.test(t)) return 'Book';
        return 'Buy';
    }

    /** Map the user's prompt to one of the item-image categories, or null when the
     *  request doesn't fall in laptop / flight / food. */
    function pickImageCategoryForPrompt(text) {
        const t = (text || '').toLowerCase();
        if (/laptop|notebook|macbook|chromebook|gaming pc|pc\b/.test(t)) return 'laptop';
        if (/flight|airfare|airline|trip|travel|fly to|fly from/.test(t)) return 'flight';
        if (/pizza|burger|food|meal|dinner|lunch|breakfast|eat|indian food|pasta|jalebi|dessert|restaurant/.test(t)) return 'food';
        return null;
    }

    function pickRandomImage(category) {
        const pool = ITEM_IMAGES[category];
        if (!Array.isArray(pool) || pool.length === 0) return null;
        return pool[Math.floor(Math.random() * pool.length)];
    }

    /**
     * Build a short (2-3 line) preamble to render above the agent cards. Prefer the
     * backend's own message when it's substantive; otherwise synthesize one from the
     * grouped agents so the transcript always reads like an explanation, not a bare
     * list of cards.
     */
    function composeDiscoverPreamble(backendMessage, agents, userPrompt) {
        const msg = (backendMessage || '').trim();
        // If the backend already gave us at least a sentence or two, trust it.
        if (msg && msg.length > 40) return msg;

        if (!Array.isArray(agents) || agents.length === 0) return msg;

        const soloName = state.soloAgent ? displayAgentName(state.soloAgent.agentName) : null;

        // Compact agent list, all on one line: "Name (pitch, N options)" comma-separated.
        const agentSummaries = agents.slice(0, 4).map((g) => {
            const name = displayAgentName(g.agentName);
            const pitch = (g.agentType || '').trim();
            const count = g.options.length;
            const parts = [];
            if (pitch) parts.push(pitch);
            if (count) parts.push(`${count} option${count === 1 ? '' : 's'}`);
            return parts.length ? `${name} (${parts.join(', ')})` : name;
        });

        const joined = agentSummaries.join(', ');
        const promptSnippet = truncatePrompt(userPrompt, 60);

        if (soloName) {
            return `Here's what ${soloName} came back with for "${promptSnippet}": ${joined}.`;
        }
        return `Here are the options I found for "${promptSnippet}" — ${agents.length} agent${agents.length === 1 ? '' : 's'} fit this ask: ${joined}.`;
    }

    function truncatePrompt(text, max) {
        const t = (text || '').trim();
        if (t.length <= max) return t;
        return t.slice(0, max - 1).trimEnd() + '…';
    }

    /** Group AgentOption[] into a stable order of { agentId, agentName, agentType, options[] }. */
    function groupOptionsByAgent(options) {
        const groups = new Map();
        for (const opt of options) {
            const key = opt.agent_id || opt.agent_name || '__unknown__';
            if (!groups.has(key)) {
                groups.set(key, {
                    agentId: opt.agent_id || '',
                    agentName: opt.agent_name || 'Agent',
                    agentType: opt.agent_type || '',
                    options: []
                });
            }
            groups.get(key).options.push(opt);
        }
        return Array.from(groups.values());
    }

    // ==========================================================================
    // Sidebar chats (very light — one chat per handleSubmit that starts a fresh transcript)
    // ==========================================================================

    function upsertChatFromPrompt(promptText) {
        // If this is the first turn of the current session, register a sidebar chat.
        if (!state.chats.some((c) => c.id === state.activeChatId)) {
            const chat = {
                id: makeId(),
                label: promptText.length > 40 ? promptText.slice(0, 40).trim() + '…' : promptText
            };
            state.chats.unshift(chat);
            state.activeChatId = chat.id;
        }
        renderChatList();
    }

    function renderChatList() {
        chatList.innerHTML = '';
        if (state.chats.length === 0) {
            const empty = el('li', 'nav-empty', 'No chats yet');
            chatList.appendChild(empty);
            return;
        }
        state.chats.forEach((chat) => {
            const li = el('li');
            if (chat.id === state.activeChatId) li.classList.add('active');
            li.innerHTML = ICON_CHAT_LIST;
            const label = el('span', null, chat.label);
            li.appendChild(label);
            li.addEventListener('click', () => {
                // Selecting a different chat resets to a new session — this sample
                // doesn't persist per-chat transcripts.
                if (chat.id !== state.activeChatId) startNewChat();
            });
            chatList.appendChild(li);
        });
    }

    function startNewChat() {
        state.sessionId = null;
        state.activeChatId = null;
        state.turns = [];
        state.purchaseInFlight = false;
        state.suggestionActionLabel = 'Buy';
        state.soloAgent = null;
        sessionPill.hidden = true;
        sessionIdText.textContent = '';
        welcomeEl.hidden = false;
        messagesEl.hidden = true;
        promptEl.value = '';
        autosizePrompt();
        setSendEnabled(false);
        updateComposerForSolo();
        renderChatList();
        render();
    }

    // ==========================================================================
    // Purchase flow — driven by real /execute and /confirm
    //
    // Stages:
    //   'executing'  → we've POSTed /execute, waiting for response
    //   'confirm'    → /execute succeeded, showing address+payment+total+Pay
    //   'confirming' → we've POSTed /confirm, waiting for confirmation
    //   'success'    → confirmed; brief green check card
    //   'tracking'   → persistent order tracking card
    //   'error'      → API call failed
    // ==========================================================================

    async function handleBuyOption(option, actionLabel) {
        if (state.purchaseInFlight) return;
        state.purchaseInFlight = true;
        const flowId = makeId();
        pushTurn({
            id: flowId,
            type: 'purchase',
            stage: 'executing',
            option,
            actionLabel: actionLabel || state.suggestionActionLabel,
            title: optionTitle(option),
            detail: optionDetail(option),
            price: option.price || ''
        });

        try {
            const resp = await callApi('/api/orchestrator/execute', {
                userId: state.userId,
                sessionId: state.sessionId,
                optionId: option.option_id,
                prompt: ''
            });
            // Update — the confirm card uses the execute response's message as the summary.
            updateTurn(flowId, {
                stage: 'confirm',
                executeResp: resp,
                summary: (resp && resp.message) || ''
            });
        } catch (err) {
            state.purchaseInFlight = false;
            updateTurn(flowId, {
                stage: 'error',
                errorText: err && err.message ? err.message : String(err)
            });
        }
    }

    async function handleConfirmPurchase(flowId) {
        const turn = state.turns.find((t) => t.id === flowId);
        if (!turn) return;
        // Kick off the cycling loader labels so the user sees a progression through
        // "identity → payment → address" while the confirm call is in flight.
        updateTurn(flowId, { stage: 'confirming', loaderLabel: CONFIRM_LOADER_STEPS[0] });
        let loaderIdx = 0;
        const loaderTimer = setInterval(() => {
            loaderIdx = Math.min(loaderIdx + 1, CONFIRM_LOADER_STEPS.length - 1);
            updateTurn(flowId, { loaderLabel: CONFIRM_LOADER_STEPS[loaderIdx] });
        }, 700);
        // Enforce a minimum visible time so all three loader phases render even when
        // the API responds instantly.
        const minDelay = new Promise((r) => setTimeout(r, 2100));
        try {
            const [resp] = await Promise.all([
                callApi('/api/orchestrator/confirm', {
                    userId: state.userId,
                    sessionId: state.sessionId,
                    optionId: turn.option.option_id,
                    prompt: 'Please confirm this booking.'
                }),
                minDelay
            ]);
            clearInterval(loaderTimer);
            const orderNumber = (resp && resp.confirmation && resp.confirmation.confirmationId) || makeOrderNumber();
            const statusLabel = (resp && resp.message) || 'Order placed';
            updateTurn(flowId, {
                stage: 'success',
                confirmResp: resp,
                orderNumber,
                statusLabel
            });
            // After a short beat, flip to permanent tracking view + toast.
            setTimeout(() => {
                updateTurn(flowId, { stage: 'tracking' });
                pushToast(statusLabel, `${turn.title} · ${turn.price}`);
                state.purchaseInFlight = false;
            }, 900);
        } catch (err) {
            clearInterval(loaderTimer);
            state.purchaseInFlight = false;
            updateTurn(flowId, {
                stage: 'error',
                errorText: err && err.message ? err.message : String(err)
            });
        }
    }

    function handleCancelPurchase(flowId) {
        state.purchaseInFlight = false;
        removeTurn(flowId);
    }

    // ==========================================================================
    // Toasts
    // ==========================================================================

    function pushToast(title, message) {
        const id = makeId();
        state.toasts.push({ id, title, message });
        renderToasts();
        setTimeout(() => dismissToast(id), 5000);
    }

    function dismissToast(id) {
        state.toasts = state.toasts.filter((t) => t.id !== id);
        renderToasts();
    }

    function renderToasts() {
        toastStackEl.innerHTML = '';
        state.toasts.forEach((toast) => {
            const wrap = el('div', 'toast');
            const body = el('div', 'toast-body');
            body.appendChild(el('div', 'toast-title', toast.title));
            body.appendChild(el('div', 'toast-message', toast.message));
            wrap.appendChild(body);
            const close = el('button', 'toast-close');
            close.type = 'button';
            close.setAttribute('aria-label', 'Dismiss');
            close.innerHTML = ICON_CLOSE;
            close.addEventListener('click', () => dismissToast(toast.id));
            wrap.appendChild(close);
            toastStackEl.appendChild(wrap);
        });
    }

    // ==========================================================================
    // Render pipeline
    // ==========================================================================

    function render() {
        renderTranscript();
    }

    function renderTranscript() {
        messagesInner.innerHTML = '';
        for (const turn of state.turns) {
            const node = renderTurn(turn);
            if (node) messagesInner.appendChild(node);
        }
        requestAnimationFrame(() => {
            messagesEl.scrollTo({ top: messagesEl.scrollHeight, behavior: 'smooth' });
        });
    }

    function renderTurn(turn) {
        switch (turn.type) {
            case 'user':          return renderUserTurn(turn.text);
            case 'thinking':      return renderThinkingTurn(turn.label);
            case 'orchestration': return renderOrchestrationTurn(turn);
            case 'purchase':      return renderPurchaseTurn(turn);
            case 'assistant':     return renderAssistantTurn(turn.text);
            case 'solo-banner':   return renderSoloBanner(turn);
            case 'error':         return renderErrorTurn(turn.text);
            default:              return null;
        }
    }

    // ---- Basic turn renderers ---------------------------------------------------

    function renderUserTurn(text) {
        const wrap = el('div', 'user-turn');
        wrap.appendChild(el('div', 'user-bubble', text));
        return wrap;
    }

    function renderAssistantTurn(text) {
        return el('div', 'assistant-response', text);
    }

    function renderErrorTurn(text) {
        const wrap = el('div', 'assistant-response error-response');
        wrap.appendChild(el('strong', null, 'Request failed. '));
        wrap.appendChild(document.createTextNode(text));
        return wrap;
    }

    function renderThinkingTurn(label) {
        const wrap = el('div', 'thinking');
        const shimmer = el('span', 'thinking-shimmer', label || 'Thinking…');
        wrap.appendChild(shimmer);
        const dots = el('span', 'thinking-dots');
        dots.innerHTML = '<span></span><span></span><span></span>';
        wrap.appendChild(dots);
        return wrap;
    }

    // ---- Solo-agent banner ("You're now chatting with X Agent") -----------------

    function renderSoloBanner(turn) {
        const wrap = el('div', 'solo-banner');

        const divider = el('div', 'solo-divider');
        divider.appendChild(el('span', 'solo-divider-line'));
        divider.appendChild(el('span', 'solo-divider-text',
            `You're now chatting with ${displayAgentName(turn.agentName)} Agent`));
        divider.appendChild(el('span', 'solo-divider-line'));
        wrap.appendChild(divider);

        const askLabel = el('div', 'solo-ask', 'Ask a question, or try:');
        wrap.appendChild(askLabel);

        const chipRow = el('div', 'solo-chips');
        (turn.samples || []).forEach((prompt) => {
            const chip = el('button', 'solo-chip', prompt);
            chip.type = 'button';
            chip.addEventListener('click', () => {
                if (!state.soloAgent) return; // exited before click
                handleSubmit(prompt);
            });
            chipRow.appendChild(chip);
        });
        wrap.appendChild(chipRow);
        return wrap;
    }

    function activateSoloAgent(group, imageCategory) {
        state.soloAgent = {
            agentId: group.agentId,
            agentName: group.agentName,
            imageCategory: imageCategory || null
        };
        const samples = SOLO_SAMPLE_PROMPTS[imageCategory || 'default']
            || SOLO_SAMPLE_PROMPTS.default;
        pushTurn({
            id: makeId(),
            type: 'solo-banner',
            agentName: group.agentName,
            samples
        });
        updateComposerForSolo();
        // Re-render existing orchestration turns so the "Chat with agent" pill hides
        // on the card that's now the active solo agent.
        render();
    }

    function exitSoloAgent() {
        state.soloAgent = null;
        updateComposerForSolo();
        render();
    }

    function updateComposerForSolo() {
        // Chip inside the composer for the active solo agent, with an × to exit.
        composerTagsEl.innerHTML = '';
        if (state.soloAgent) {
            const short = displayAgentName(state.soloAgent.agentName);
            const chip = el('span', 'composer-tag');
            chip.appendChild(document.createTextNode(`@${short}`));
            const close = el('button', null);
            close.type = 'button';
            close.setAttribute('aria-label', 'Exit chat with agent');
            close.innerHTML = ICON_CLOSE;
            close.addEventListener('click', exitSoloAgent);
            chip.appendChild(close);
            composerTagsEl.appendChild(chip);
            promptEl.placeholder = `Message ${short}`;
        } else {
            promptEl.placeholder = 'Message Copilot… (Enter to send, Shift+Enter for a new line)';
        }
    }

    // ---- Orchestration turn (analysis + agent cards) ----------------------------

    function renderOrchestrationTurn(turn) {
        const wrap = el('div', 'orchestration');
        if (turn.message) {
            wrap.appendChild(el('div', 'assistant-response analysis', turn.message));
        }
        const cards = el('div', 'orchestration-cards');
        turn.agents.forEach((group, index) => {
            const tone = AGENT_TONES[index % AGENT_TONES.length];
            cards.appendChild(renderAgentResultCard(group, tone, turn.actionLabel, turn.imageCategory));
        });
        wrap.appendChild(cards);
        return wrap;
    }

    function renderAgentResultCard(group, tone, actionLabel, imageCategory) {
        const card = el('div', 'card');

        // Header: identity.
        const header = el('div', 'card-header');
        const identity = el('div', 'agent-identity');
        identity.appendChild(renderAvatar(group.agentId, group.agentName, tone, 32));
        const identityText = el('div', 'agent-identity-text');
        identityText.appendChild(el('span', 'agent-name', displayAgentName(group.agentName)));
        if (group.agentType) {
            const pitch = el('p', 'agent-pitch');
            pitch.appendChild(el('span', 'agent-pitch-lead', group.agentType));
            pitch.appendChild(document.createTextNode(` · ${group.options.length} option${group.options.length === 1 ? '' : 's'} in this response`));
            identityText.appendChild(pitch);
        }
        identity.appendChild(identityText);
        header.appendChild(identity);

        // "Chat with agent" pill — activates solo mode so subsequent prompts route
        // only to this agent. Hidden when we're already talking to this agent solo.
        if (!state.soloAgent || state.soloAgent.agentId !== group.agentId) {
            const chatBtn = el('button', 'chat-with-agent-btn');
            chatBtn.type = 'button';
            chatBtn.title = `Chat with ${displayAgentName(group.agentName)} only`;
            chatBtn.innerHTML =
                '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>' +
                '<span>Chat with agent</span>';
            chatBtn.addEventListener('click', () => activateSoloAgent(group, imageCategory));
            header.appendChild(chatBtn);
        }
        card.appendChild(header);

        // Body: pick list — one row per option.
        const body = el('div', 'card-body');
        const list = el('ul', 'pick-list');
        group.options.forEach((opt) => list.appendChild(renderPickRow(opt, actionLabel, imageCategory)));
        body.appendChild(list);
        card.appendChild(body);

        return card;
    }

    function renderPickRow(option, actionLabel, imageCategory) {
        const li = el('li', 'pick-row');
        const main = el('div', 'pick-main');

        // Category-based product image when the prompt is for laptop / flight / food;
        // otherwise fall back to an initials placeholder (server never supplies imagery).
        // The image is picked once at discover time and memoized on the option so
        // re-renders (Pay/Confirm state changes) don't swap thumbnails.
        let imgSrc = option._imgSrc || null;
        if (!imgSrc && imageCategory) {
            imgSrc = pickRandomImage(imageCategory);
            option._imgSrc = imgSrc;
        }
        if (imgSrc) {
            const img = document.createElement('img');
            img.className = 'pick-thumb';
            img.src = imgSrc;
            img.alt = option.agent_name || 'Option';
            img.loading = 'lazy';
            main.appendChild(img);
        } else {
            const placeholder = el('div', 'pick-thumb pick-thumb-placeholder');
            placeholder.textContent = initials(option.agent_name);
            main.appendChild(placeholder);
        }

        // Title + detail. Prefer server-provided title/description so we surface
        // the concrete offer (e.g. "Margherita Pizza (12in)") instead of guessing
        // from a free-form details string.
        const title = optionTitle(option);
        const rest = optionDetail(option);
        const name = el('div', 'pick-name');
        const headline = el('div', 'pick-headline');
        headline.appendChild(el('span', 'pick-title', title));
        name.appendChild(headline);
        if (rest) {
            const detail = el('span', 'pick-detail');
            detail.textContent = rest.length > 140 ? rest.slice(0, 140).trim() + '…' : rest;
            name.appendChild(detail);
        }

        // Expandable "more info" if the details were truncated.
        let moreInfo = null;
        if (rest && rest.length > 140) {
            moreInfo = el('p', 'more-info', rest);
            moreInfo.hidden = true;
            name.appendChild(moreInfo);
        }

        main.appendChild(name);
        li.appendChild(main);

        // Actions column: price + Buy/Order + chevron.
        const actions = el('div', 'pick-actions');
        if (option.price) actions.appendChild(el('span', 'pick-price', option.price));

        const buyBtn = el('button', 'btn btn-primary', actionLabel || 'Buy');
        buyBtn.type = 'button';
        buyBtn.disabled = state.purchaseInFlight;
        buyBtn.addEventListener('click', () => handleBuyOption(option, actionLabel));
        actions.appendChild(buyBtn);

        if (moreInfo) {
            const chev = el('button', 'btn btn-subtle btn-icon-only');
            chev.type = 'button';
            chev.setAttribute('aria-label', 'View details');
            chev.innerHTML = `<span class="icon-chevron">${ICON_CHEVRON_RIGHT}</span>`;
            let isOpen = false;
            chev.addEventListener('click', () => {
                isOpen = !isOpen;
                chev.querySelector('.icon-chevron').classList.toggle('open', isOpen);
                moreInfo.hidden = !isOpen;
            });
            actions.appendChild(chev);
        }
        li.appendChild(actions);
        return li;
    }

    // ---- Purchase turn ----------------------------------------------------------

    function renderPurchaseTurn(turn) {
        switch (turn.stage) {
            case 'executing':  return renderPurchaseLoading(turn, 'Contacting the agent…');
            case 'confirming': return renderPurchaseLoading(turn, turn.loaderLabel || CONFIRM_LOADER_STEPS[0]);
            case 'confirm':    return renderPurchaseConfirm(turn);
            case 'success':    return renderPurchaseSuccess(turn);
            case 'tracking':   return renderOrderTracking(turn);
            case 'error':      return renderPurchaseError(turn);
            default:           return null;
        }
    }

    function renderPurchaseWrap(turn) {
        const wrap = el('div', 'purchase-wrap');
        wrap.appendChild(renderUserTurn(`${turn.actionLabel || 'Buy'} the ${turn.title}`));
        return wrap;
    }

    function renderPurchaseLoading(turn, label) {
        const wrap = renderPurchaseWrap(turn);
        wrap.appendChild(renderThinkingTurn(label));
        return wrap;
    }

    // Small "Edit" affordance rendered at the end of a detail row. Uses a native
    // prompt() to keep the sample lightweight — the new value is stored on the turn
    // as an override so it survives re-renders and is reflected in Pay flow.
    function renderEditCta(onClick) {
        const btn = el('button', 'edit-cta', 'Edit');
        btn.type = 'button';
        btn.addEventListener('click', onClick);
        return btn;
    }

    function editConfirmField(turn, key, currentValue, label) {
        const next = window.prompt(`Edit ${label}`, currentValue);
        if (next === null) return; // cancelled
        const trimmed = next.trim();
        if (!trimmed || trimmed === currentValue) return;
        const overrides = { ...(turn.confirmOverrides || {}), [key]: trimmed };
        updateTurn(turn.id, { confirmOverrides: overrides });
    }

    function renderPurchaseConfirm(turn) {
        const wrap = renderPurchaseWrap(turn);
        const card = el('div', 'card');

        const header = el('div', 'card-header');
        header.appendChild(el('div', 'card-header-title', 'Confirm and pay'));
        card.appendChild(header);

        const body = el('div', 'card-body');

        // Pull the agent's structured preview from the /execute response. Falls back to the
        // option we already had when the agent didn't return that field (e.g. old cache).
        const preview = extractExecutePreview(turn.executeResp);
        const previewTitle = preview.title || turn.title;
        const previewDetail = preview.description || turn.detail;
        const previewPrice = formatPreviewPrice(preview, turn.price);

        // Item row (no static imagery — the agent provides the text; the icon
        // slot stays empty so the layout still reads as a product line).
        const item = el('div', 'item-row');
        const itemText = el('div', 'item-text');
        itemText.appendChild(el('div', 'item-title', previewTitle));
        if (previewDetail) itemText.appendChild(el('div', 'item-detail', previewDetail));
        item.appendChild(itemText);
        if (previewPrice) item.appendChild(el('span', 'item-price', previewPrice));
        body.appendChild(item);

        if (turn.summary && !preview.title) {
            // Only show the raw summary when we couldn't extract a structured preview —
            // otherwise the fields above already say the same thing.
            const summary = el('p', 'more-info', turn.summary);
            body.appendChild(summary);
        }

        // Domain-specific attribute rows (seat/meal for flights, toppings for pizza, etc.).
        const attrRows = buildPreviewAttributeRows(preview);
        attrRows.forEach((row) => body.appendChild(row));

        // Delivery address — the agent's structured preview wins when present, otherwise
        // we show a demo address so the confirm card always reads as a complete order.
        // A per-turn override lets the user tap "Edit" to change either value in place.
        const overrides = turn.confirmOverrides || {};
        const deliveryAddress = overrides.delivery_address
            || preview.delivery_address
            || '742 Evergreen Terrace, Springfield, IL 62704';
        const addressRow = el('div', 'detail-row');
        addressRow.innerHTML = `<span class="detail-icon">${ICON_LOCATION}</span>`;
        addressRow.appendChild(el('div', 'detail-text', deliveryAddress));
        addressRow.appendChild(el('span', 'detail-hint', 'Delivery address'));
        addressRow.appendChild(renderEditCta(() => editConfirmField(turn, 'delivery_address', deliveryAddress, 'delivery address')));
        body.appendChild(addressRow);

        // Payment — same pattern: prefer the agent's value, otherwise show a demo card.
        const paymentMethod = overrides.payment_method
            || preview.payment_method
            || 'Visa •••• 4242 (Personal)';
        const payment = el('div', 'detail-row');
        payment.innerHTML = `<span class="detail-icon">${ICON_PAYMENT}</span>`;
        payment.appendChild(el('div', 'detail-text', paymentMethod));
        payment.appendChild(el('span', 'detail-hint', 'Saved payment'));
        payment.appendChild(renderEditCta(() => editConfirmField(turn, 'payment_method', paymentMethod, 'payment method')));
        body.appendChild(payment);

        if (previewPrice) {
            const total = el('div', 'total-row');
            total.appendChild(el('span', 'total-label', 'Total'));
            total.appendChild(el('span', 'total-value', previewPrice));
            body.appendChild(total);
        }

        card.appendChild(body);

        const footer = el('div', 'card-footer');
        const cancel = el('button', 'btn btn-secondary', 'Cancel');
        cancel.type = 'button';
        cancel.addEventListener('click', () => handleCancelPurchase(turn.id));
        footer.appendChild(cancel);

        const pay = el('button', 'btn btn-primary', previewPrice ? `Pay ${previewPrice}` : 'Confirm');
        pay.type = 'button';
        pay.addEventListener('click', () => handleConfirmPurchase(turn.id));
        footer.appendChild(pay);
        card.appendChild(footer);

        wrap.appendChild(card);
        return wrap;
    }

    /**
     * Extract the structured preview the backend attaches to /execute responses. The workflow
     * parses the agent's JSON reply and merges it into `confirmation.data`, so we look there
     * first; falls back to an empty object when nothing structured came back.
     */
    function extractExecutePreview(resp) {
        const data = resp && resp.confirmation && resp.confirmation.data;
        if (!data || typeof data !== 'object') return {};

        // Strings we want to surface directly.
        const scalarKeys = [
            'title', 'description', 'delivery_address', 'payment_method',
            'customer_name', 'guest_name', 'passenger_name', 'phone',
            'seat', 'cabin', 'meal', 'departure', 'arrival',
            'check_in', 'check_out', 'room_type', 'eta',
            'party_size', 'reservation_time', 'currency'
        ];
        const preview = {};
        for (const key of scalarKeys) {
            const v = data[key];
            if (v !== undefined && v !== null && String(v).trim() !== '') preview[key] = String(v);
        }

        // Numeric price — may arrive as number or string (System.Text.Json decimal).
        if (data.price !== undefined && data.price !== null && data.price !== '') {
            const n = Number(data.price);
            if (!Number.isNaN(n)) preview.price = n;
        }

        // Details / items may be JSON-encoded strings (nested objects were flattened by
        // ParseExecutePreview to preserve their raw JSON). Try to reparse them here.
        for (const key of ['details', 'items']) {
            const v = data[key];
            if (v === undefined || v === null) continue;
            if (typeof v === 'string') {
                try { preview[key] = JSON.parse(v); }
                catch { preview[key] = v; }
            } else {
                preview[key] = v;
            }
        }

        return preview;
    }

    function formatPreviewPrice(preview, fallback) {
        if (preview && typeof preview.price === 'number' && !Number.isNaN(preview.price)) {
            const cur = (preview.currency || '').toUpperCase();
            const symbol = { USD: '$', EUR: '€', GBP: '£', INR: '₹', JPY: '¥' }[cur];
            return symbol ? `${symbol}${preview.price}` : `${preview.price}${cur ? ' ' + cur : ''}`;
        }
        return fallback || '';
    }

    /**
     * Build small key/value detail rows for domain-specific fields we recognize on the
     * preview (seat, meal, room type, ETA, toppings, etc.). Skipped when the preview didn't
     * include the field — we never fabricate values.
     */
    function buildPreviewAttributeRows(preview) {
        const rows = [];
        const push = (label, value) => {
            if (value === undefined || value === null || String(value).trim() === '') return;
            const row = el('div', 'detail-row');
            row.appendChild(el('div', 'detail-text', String(value)));
            row.appendChild(el('span', 'detail-hint', label));
            rows.push(row);
        };

        push('Seat', preview.seat);
        push('Cabin', preview.cabin);
        push('Meal', preview.meal);
        push('Departure', preview.departure);
        push('Arrival', preview.arrival);
        push('Check-in', preview.check_in);
        push('Check-out', preview.check_out);
        push('Room', preview.room_type);
        push('ETA', preview.eta);
        push('Party size', preview.party_size);
        push('Reservation', preview.reservation_time);

        // Details / items — render a compact one-liner from nested objects when present.
        if (preview.details && typeof preview.details === 'object' && !Array.isArray(preview.details)) {
            const parts = Object.entries(preview.details)
                .filter(([, v]) => v !== null && v !== undefined && String(v).trim() !== '')
                .map(([k, v]) => `${prettyKey(k)}: ${v}`);
            if (parts.length) push('Details', parts.join(' · '));
        }
        if (Array.isArray(preview.items) && preview.items.length) {
            const parts = preview.items
                .map((it) => (typeof it === 'object' ? [it.qty, it.name].filter(Boolean).join(' × ') : String(it)))
                .filter(Boolean);
            if (parts.length) push('Items', parts.join(', '));
        }

        return rows;
    }

    function prettyKey(k) {
        return String(k || '').replace(/[_-]+/g, ' ').replace(/^\w/, (c) => c.toUpperCase());
    }

    function renderPurchaseSuccess(turn) {
        const wrap = renderPurchaseWrap(turn);
        const row = el('div', 'success-row');
        const iconWrap = el('span', 'success-icon');
        iconWrap.innerHTML = ICON_CHECK_FILLED;
        row.appendChild(iconWrap);
        const body = el('div', 'success-body');
        body.appendChild(el('div', 'success-title', turn.statusLabel || 'Order placed'));
        body.appendChild(el('div', 'success-message', `${turn.title} · ${turn.price || ''}`));
        row.appendChild(body);
        wrap.appendChild(row);
        return wrap;
    }

    function renderOrderTracking(turn) {
        const wrap = renderPurchaseWrap(turn);
        const card = el('div', 'card order-wrap');

        const header = el('div', 'card-header');
        header.appendChild(el('div', 'card-header-title', turn.statusLabel || 'Order placed'));
        header.appendChild(el('span', 'discount-badge', `#${turn.orderNumber}`));
        card.appendChild(header);

        const body = el('div', 'card-body');

        const item = el('div', 'item-row');
        const itemText = el('div', 'item-text');
        itemText.appendChild(el('div', 'item-title', turn.title));
        if (turn.detail) itemText.appendChild(el('div', 'item-detail', turn.detail));
        item.appendChild(itemText);
        if (turn.price) item.appendChild(el('span', 'item-price', turn.price));
        body.appendChild(item);

        // 4-step stepper.
        const steps = ['Confirmed', 'Preparing', 'In transit', 'Delivered'];
        const stepper = el('div', 'stepper');
        steps.forEach((label, index) => {
            const step = el('div', 'step' + (index === 0 ? ' current' : ''));
            // "Confirmed" is the completed step — tick renders in the green success colour.
            const icon = el('span', 'step-icon' + (index === 0 ? ' done' : ''));
            icon.innerHTML = index === 0 ? ICON_CHECK_SMALL : ICON_CIRCLE_SMALL;
            step.appendChild(icon);
            const labelEl = el('span', 'step-label' + (index === 0 ? ' active' : ''), label);
            step.appendChild(labelEl);
            stepper.appendChild(step);
            if (index < steps.length - 1) stepper.appendChild(el('span', 'step-line' + (index === 0 ? ' done' : '')));
        });
        body.appendChild(stepper);
        card.appendChild(body);
        wrap.appendChild(card);
        return wrap;
    }

    function renderPurchaseError(turn) {
        const wrap = renderPurchaseWrap(turn);
        const err = el('div', 'assistant-response error-response');
        err.appendChild(el('strong', null, 'Purchase failed. '));
        err.appendChild(document.createTextNode(turn.errorText || 'Please try again.'));
        wrap.appendChild(err);
        return wrap;
    }

    // ==========================================================================
    // Composer
    // ==========================================================================

    function autosizePrompt() {
        promptEl.style.height = 'auto';
        promptEl.style.height = Math.min(promptEl.scrollHeight, 200) + 'px';
    }

    function setSendEnabled(enabled) {
        sendBtn.disabled = !enabled;
    }

    // ==========================================================================
    // Wiring
    // ==========================================================================

    function wireSuggestions() {
        // Welcome-screen suggestion cards were removed — the composer is now the only
        // entry point. Kept as a no-op to preserve boot ordering.
    }

    function wireComposer() {
        promptEl.addEventListener('input', () => {
            autosizePrompt();
            setSendEnabled(promptEl.value.trim().length > 0);
        });
        promptEl.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                if (promptEl.value.trim()) submitFromComposer();
            }
        });
        composerEl.addEventListener('submit', (e) => {
            e.preventDefault();
            submitFromComposer();
        });
    }

    function submitFromComposer() {
        const text = promptEl.value.trim();
        if (!text) return;
        promptEl.value = '';
        autosizePrompt();
        setSendEnabled(false);
        handleSubmit(text);
    }

    function wireSidebar() {
        newChatBtn.addEventListener('click', startNewChat);
        clearBtn.addEventListener('click', () => {
            state.turns = [];
            render();
        });
        userIdInput.addEventListener('change', () => {
            state.userId = userIdInput.value.trim() || 'Vardhan Malik';
            userIdInput.value = state.userId;
        });
    }

    // ==========================================================================
    // Boot
    // ==========================================================================

    function boot() {
        state.userId = userIdInput.value.trim() || 'Vardhan Malik';
        wireSuggestions();
        wireComposer();
        wireSidebar();
        renderChatList();
        autosizePrompt();
        setSendEnabled(false);
        checkHealth();
        setInterval(checkHealth, 30000);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot);
    } else {
        boot();
    }
})();
