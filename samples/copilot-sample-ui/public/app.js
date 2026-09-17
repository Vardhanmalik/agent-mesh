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

    /** Starter prompts shown on the welcome screen. Same wording as the Agent Mesh prototype. */
    const SUGGESTIONS = [
        {
            id: 'laptop',
            label: 'Find a gaming laptop',
            desc: 'Under $1,000 — RTX 4060, 16GB, 144Hz',
            prompt: 'Find me a gaming laptop under $1,000: strong GPU, 16GB RAM, 144Hz, good for video editing too.',
            actionLabel: 'Buy',
            gradient: 'linear-gradient(135deg,#4f6bed,#7c8cf8)',
            iconSvg: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="white" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="4" width="20" height="14" rx="2"/><path d="M2 20h20"/></svg>'
        },
        {
            id: 'pizza',
            label: 'Order pizza tonight',
            desc: 'Delivered within the hour',
            prompt: 'I feel like having pizza tonight, something to arrive in the next hour.',
            actionLabel: 'Order',
            gradient: 'linear-gradient(135deg,#e8578c,#f38aa8)',
            iconSvg: '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="white" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M15 11h.01M11 15h.01M16 16h.01M2 12l10-10 10 10-10 10z"/></svg>'
        }
    ];

    const AGENT_TONES = ['brand', 'success', 'warning'];

    /**
     * Product photography, keyed by a keyword to look for inside the option
     * details / option id. First match wins. Purely cosmetic — the API doesn't
     * return image URLs.
     */
    const IMAGE_KEYWORDS = [
        // Laptops
        { kw: 'legion slim', src: '/assets/agent-mesh/laptop-legion-slim-5.jpg' },
        { kw: 'legion pro', src: '/assets/agent-mesh/laptop-legion-pro-5.jpg' },
        { kw: 'loq', src: '/assets/agent-mesh/laptop-loq-15.jpg' },
        { kw: 'legion', src: '/assets/agent-mesh/laptop-legion-slim-5.jpg' },
        { kw: 'omen transcend', src: '/assets/agent-mesh/laptop-omen-transcend-14.jpg' },
        { kw: 'omen 16', src: '/assets/agent-mesh/laptop-omen-16.jpg' },
        { kw: 'omen', src: '/assets/agent-mesh/laptop-omen-16.jpg' },
        { kw: 'victus', src: '/assets/agent-mesh/laptop-hp-victus-15.jpg' },
        { kw: 'alienware', src: '/assets/agent-mesh/laptop-alienware-m16.jpg' },
        { kw: 'g16', src: '/assets/agent-mesh/laptop-dell-g16.jpg' },
        { kw: 'g15', src: '/assets/agent-mesh/laptop-dell-g15.jpg' },
        { kw: 'dell', src: '/assets/agent-mesh/laptop-dell-g15.jpg' },
        { kw: 'hp', src: '/assets/agent-mesh/laptop-hp-victus-15.jpg' },

        // Pizzas
        { kw: 'hawaiian', src: '/assets/agent-mesh/pizza-hawaiian.jpg' },
        { kw: 'pepperoni', src: '/assets/agent-mesh/pizza-pepperoni.jpg' },
        { kw: 'veggie', src: '/assets/agent-mesh/pizza-veggie.jpg' },
        { kw: 'vegetarian', src: '/assets/agent-mesh/pizza-veggie.jpg' },
        { kw: 'margherita', src: '/assets/agent-mesh/pizza-margherita.jpg' },
        { kw: 'slice', src: '/assets/agent-mesh/pizza-board.jpg' },
        { kw: "joe's", src: '/assets/agent-mesh/pizza-classic.jpg' },
        { kw: 'classic pie', src: '/assets/agent-mesh/pizza-classic.jpg' },
        { kw: 'pizza hut', src: '/assets/agent-mesh/pizza-hawaiian.jpg' },
        { kw: 'blaze', src: '/assets/agent-mesh/pizza-veggie.jpg' },
        { kw: 'mod', src: '/assets/agent-mesh/pizza-pepperoni.jpg' },
        { kw: 'pizza', src: '/assets/agent-mesh/pizza-classic.jpg' }
    ];

    /** Brand logos, matched against agent_name / agent_id (case-insensitive substring). */
    const LOGO_KEYWORDS = [
        { kw: 'legion', src: '/assets/agent-mesh/logos/lenovo.svg' },
        { kw: 'lenovo', src: '/assets/agent-mesh/logos/lenovo.svg' },
        { kw: 'omen', src: '/assets/agent-mesh/logos/hp.svg' },
        { kw: 'hp', src: '/assets/agent-mesh/logos/hp.svg' },
        { kw: 'alienware', src: '/assets/agent-mesh/logos/alienware.svg' },
        { kw: 'dell', src: '/assets/agent-mesh/logos/dell.svg' },
        { kw: 'doordash', src: '/assets/agent-mesh/logos/doordash.svg' },
        { kw: 'uber', src: '/assets/agent-mesh/logos/ubereats.svg' }
    ];

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

    function pickImageFor(option) {
        const hay = `${option.option_id || ''} ${option.details || ''} ${option.agent_name || ''} ${option.agent_type || ''}`;
        return firstMatch(IMAGE_KEYWORDS, hay);
    }

    function logoFor(agentName, agentId) {
        return firstMatch(LOGO_KEYWORDS, `${agentId || ''} ${agentName || ''}`);
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

    /** Try to make a nice short agent name (drop the trailing " Agent" that many
     *  backends append). Preserve the original for the avatar's alt. */
    function displayAgentName(agentName) {
        return (agentName || 'Agent').replace(/\s+agent$/i, '').trim() || agentName || 'Agent';
    }

    function renderAvatar(agentId, agentName, tone, size /* 32|24|16 */) {
        const wrapper = el('span', `avatar tone-${tone} size-${size}`);
        const logo = logoFor(agentName, agentId);
        if (logo) {
            const img = el('img');
            img.src = logo;
            img.alt = '';
            img.onerror = () => { img.remove(); wrapper.textContent = initials(agentName); };
            wrapper.appendChild(img);
        } else {
            wrapper.textContent = initials(agentName);
        }
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
        userId: 'demo-user',
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
    const suggestionsEl = $('suggestions');
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
                context: { source: 'copilot-agent-mesh-sample' }
            });
            if (resp && resp.sessionId) {
                state.sessionId = resp.sessionId;
                sessionPill.hidden = false;
                sessionIdText.textContent = resp.sessionId;
            }
            removeTurn(thinkingId);

            const status = (resp && resp.status) || '';
            const message = (resp && resp.message) || '';
            const options = Array.isArray(resp && resp.options) ? resp.options : [];

            if (status === 'Informational' || (status === 'NeedsAddress' && !options.length)) {
                pushTurn({ id: makeId(), type: 'assistant', text: message || 'Okay.' });
                return;
            }
            if (!options.length) {
                pushTurn({
                    id: makeId(),
                    type: 'assistant',
                    text: message || 'No agents returned options for that request. Try refining the prompt.'
                });
                return;
            }
            pushTurn({
                id: makeId(),
                type: 'orchestration',
                message,
                agents: groupOptionsByAgent(options),
                actionLabel: state.suggestionActionLabel
            });
        } catch (err) {
            removeTurn(thinkingId);
            pushTurn({ id: makeId(), type: 'error', text: err && err.message ? err.message : String(err) });
        }
    }

    function pickActionLabelForPrompt(text) {
        const t = (text || '').toLowerCase();
        if (/pizza|food|deliver|dinner|lunch|breakfast|meal|eat/.test(t)) return 'Order';
        if (/flight|hotel|book|reservation|stay|trip|travel/.test(t)) return 'Book';
        return 'Buy';
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
        sessionPill.hidden = true;
        sessionIdText.textContent = '';
        welcomeEl.hidden = false;
        messagesEl.hidden = true;
        promptEl.value = '';
        autosizePrompt();
        setSendEnabled(false);
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
        const info = splitDetails(option.details);
        pushTurn({
            id: flowId,
            type: 'purchase',
            stage: 'executing',
            option,
            actionLabel: actionLabel || state.suggestionActionLabel,
            title: info.title,
            detail: info.rest,
            image: pickImageFor(option),
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
        updateTurn(flowId, { stage: 'confirming' });
        try {
            const resp = await callApi('/api/orchestrator/confirm', {
                userId: state.userId,
                sessionId: state.sessionId,
                optionId: turn.option.option_id,
                prompt: 'Please confirm this booking.'
            });
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

    // ---- Orchestration turn (analysis + agent cards) ----------------------------

    function renderOrchestrationTurn(turn) {
        const wrap = el('div', 'orchestration');
        if (turn.message) {
            wrap.appendChild(el('div', 'assistant-response analysis', turn.message));
        }
        const cards = el('div', 'orchestration-cards');
        turn.agents.forEach((group, index) => {
            const tone = AGENT_TONES[index % AGENT_TONES.length];
            cards.appendChild(renderAgentResultCard(group, tone, turn.actionLabel));
        });
        wrap.appendChild(cards);
        return wrap;
    }

    function renderAgentResultCard(group, tone, actionLabel) {
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
        card.appendChild(header);

        // Body: pick list — one row per option.
        const body = el('div', 'card-body');
        const list = el('ul', 'pick-list');
        group.options.forEach((opt) => list.appendChild(renderPickRow(opt, actionLabel)));
        body.appendChild(list);
        card.appendChild(body);

        return card;
    }

    function renderPickRow(option, actionLabel) {
        const li = el('li', 'pick-row');
        const main = el('div', 'pick-main');

        // Thumbnail (heuristic image lookup).
        const img = el('img', 'pick-thumb');
        const src = pickImageFor(option);
        if (src) {
            img.src = src;
            img.alt = '';
            img.loading = 'lazy';
            img.onerror = () => img.remove();
            main.appendChild(img);
        } else {
            const placeholder = el('div', 'pick-thumb pick-thumb-placeholder');
            placeholder.textContent = initials(option.agent_name);
            main.appendChild(placeholder);
        }

        // Title + detail.
        const info = splitDetails(option.details);
        const name = el('div', 'pick-name');
        const headline = el('div', 'pick-headline');
        headline.appendChild(el('span', 'pick-title', info.title));
        name.appendChild(headline);
        if (info.rest) {
            const detail = el('span', 'pick-detail');
            detail.textContent = info.rest.length > 140 ? info.rest.slice(0, 140).trim() + '…' : info.rest;
            name.appendChild(detail);
        }

        // Expandable "more info" if the details were truncated.
        let moreInfo = null;
        if (info.rest && info.rest.length > 140) {
            moreInfo = el('p', 'more-info', info.rest);
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
            case 'confirming': return renderPurchaseLoading(turn, 'Confirming with the agent…');
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

        // Item row.
        const item = el('div', 'item-row');
        if (turn.image) {
            const img = el('img', 'item-thumb');
            img.src = turn.image;
            img.alt = '';
            img.onerror = () => img.remove();
            item.appendChild(img);
        }
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

        // Detail rows: delivery address (only when the domain has one) + payment.
        const deliveryAddress = preview.delivery_address
            || (turn.executeResp && turn.executeResp.metadata && turn.executeResp.metadata.deliveryAddress)
            || '';
        if (deliveryAddress) {
            const address = el('div', 'detail-row');
            address.innerHTML = `<span class="detail-icon">${ICON_LOCATION}</span>`;
            address.appendChild(el('div', 'detail-text', deliveryAddress));
            address.appendChild(el('span', 'detail-hint', 'Delivery address'));
            body.appendChild(address);
        }

        const paymentMethod = preview.payment_method || 'Payment method on file';
        const payment = el('div', 'detail-row');
        payment.innerHTML = `<span class="detail-icon">${ICON_PAYMENT}</span>`;
        payment.appendChild(el('div', 'detail-text', paymentMethod));
        payment.appendChild(el('span', 'detail-hint', 'Saved payment'));
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
        if (turn.image) {
            const img = el('img', 'item-thumb');
            img.src = turn.image;
            img.alt = '';
            img.onerror = () => img.remove();
            item.appendChild(img);
        }
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
            const icon = el('span', 'step-icon');
            icon.innerHTML = index === 0 ? ICON_CHECK_SMALL : ICON_CIRCLE_SMALL;
            step.appendChild(icon);
            step.appendChild(el('span', 'step-label', label));
            stepper.appendChild(step);
            if (index < steps.length - 1) stepper.appendChild(el('span', 'step-line'));
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

    function seedSuggestion(id) {
        const suggestion = SUGGESTIONS.find((s) => s.id === id);
        if (!suggestion) return;
        state.suggestionActionLabel = suggestion.actionLabel;
        promptEl.value = suggestion.prompt;
        autosizePrompt();
        setSendEnabled(true);
        promptEl.focus();
        handleSubmit(suggestion.prompt);
        promptEl.value = '';
        autosizePrompt();
        setSendEnabled(false);
    }

    // ==========================================================================
    // Wiring
    // ==========================================================================

    function wireSuggestions() {
        if (!suggestionsEl) return;
        // Re-render suggestions from SUGGESTIONS so they stay in sync with data.
        suggestionsEl.innerHTML = '';
        SUGGESTIONS.forEach((s) => {
            const btn = el('button', 'suggestion-card');
            btn.type = 'button';
            btn.dataset.scenario = s.id;
            const icon = el('div', 'sg-icon');
            icon.style.background = s.gradient;
            icon.innerHTML = s.iconSvg;
            const body = el('div', 'sg-body');
            body.appendChild(el('div', 'sg-title', s.label));
            body.appendChild(el('div', 'sg-desc', s.desc));
            btn.appendChild(icon);
            btn.appendChild(body);
            btn.addEventListener('click', () => seedSuggestion(s.id));
            suggestionsEl.appendChild(btn);
        });
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
            state.userId = userIdInput.value.trim() || 'demo-user';
            userIdInput.value = state.userId;
        });
    }

    // ==========================================================================
    // Boot
    // ==========================================================================

    function boot() {
        state.userId = userIdInput.value.trim() || 'demo-user';
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
