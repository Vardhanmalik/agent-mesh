/* ==========================================================================
 * Edge + Copilot extension — Sample UI client
 *
 * Simulates Microsoft Edge with a Copilot extension button. When the button
 * is clicked, we:
 *   1. Read the current tab URL from the address bar
 *   2. Classify the host into a domain (food delivery / shopping / travel / ...)
 *   3. Build a generic prompt for that domain
 *   4. POST to /api/orchestrator/discover
 *   5. Render top 3 agent options in the side panel, or "agent not found"
 *
 * No server-side changes required — this rides on the existing /discover API.
 * ========================================================================== */

(() => {
    'use strict';

    // ------------------------------------------------------------------
    // Host → domain registry
    // ------------------------------------------------------------------
    // Each entry defines:
    //   - domain:      short label shown to the user
    //   - chip:        css class for the colored pill
    //   - favicon:     bg color + letter used for tab/favicon glyph
    //   - defaultPrompt: generic prompt sent to /discover if user leaves the
    //                    panel textarea empty
    //   - pageTitle / pageHeroSubtitle: text for the mocked page render
    //   - color:       hero background gradient
    const HOST_REGISTRY = {
        // ---------- Food delivery ----------
        'doordash.com':  makeEntry('food delivery', 'food', 'D', '#ff3008', '#b32000',
            'Order me a family-size dinner for delivery under $40 with fast ETA.'),
        'ubereats.com':  makeEntry('food delivery', 'food', 'U', '#06c167', '#04884a',
            'Find me 3 highly rated dinner options for delivery within 30 minutes.'),
        'swiggy.com':    makeEntry('food delivery', 'food', 'S', '#fc8019', '#c05f10',
            'Order me a vegetarian dinner combo under ₹500 with fast delivery.'),
        'zomato.com':    makeEntry('food delivery', 'food', 'Z', '#cb202d', '#8a1620',
            'Find me the top 3 dinner options nearby with 4+ star rating.'),
        'grubhub.com':   makeEntry('food delivery', 'food', 'G', '#ff8000', '#c26200',
            'Order me lunch for one under $20 with the fastest ETA.'),

        // ---------- Shopping ----------
        'amazon.com':    makeEntry('shopping', 'shop', 'a', '#ff9900', '#b26a00',
            'Buy me 3 pairs of running shoes under $100 with prime shipping.'),
        'walmart.com':   makeEntry('shopping', 'shop', 'W', '#0071dc', '#0055a4',
            'Buy me 3 plain cotton t-shirts under $15 each with fast pickup.'),
        'target.com':    makeEntry('shopping', 'shop', 'T', '#cc0000', '#8a0000',
            'Buy me 3 kitchen essentials — a pan, spatula, and cutting board — under $60 total.'),
        'ebay.com':      makeEntry('shopping', 'shop', 'E', '#e53238', '#a02127',
            'Find me 3 refurbished laptops under $500 with high seller ratings.'),
        'myntra.com':    makeEntry('shopping', 'shop', 'M', '#ff3f6c', '#b02a49',
            'Buy me 3 casual shirts for men in size M under ₹1500 each.'),
        'flipkart.com':  makeEntry('shopping', 'shop', 'F', '#2874f0', '#1e57b5',
            'Buy me 3 wireless earbuds under ₹3000 with best ratings.'),
        'bestbuy.com':   makeEntry('shopping', 'shop', 'B', '#0046be', '#003189',
            'Buy me a wireless mouse, keyboard, and mousepad under $100 total.'),

        // ---------- Travel ----------
        'expedia.com':   makeEntry('travel', 'travel', 'E', '#fdd93d', '#a58a10',
            'Find me a flight from Seattle to New York next Friday morning, window seat.'),
        'kayak.com':     makeEntry('travel', 'travel', 'K', '#ff690f', '#b8480a',
            'Find me the cheapest flight from Seattle to LA next weekend.'),
        'booking.com':   makeEntry('travel', 'travel', 'B', '#003580', '#00224f',
            'Book me a hotel in San Francisco for 2 nights next weekend near Union Square.'),
        'airbnb.com':    makeEntry('travel', 'travel', 'A', '#ff5a5f', '#b23e42',
            'Find me a 2-bedroom stay in Seattle next weekend under $300/night.'),

        // ---------- Dining reservations ----------
        'opentable.com': makeEntry('dining', 'dine', 'O', '#da3743', '#932128',
            'Reserve dinner for 4 at an Italian place tomorrow at 7pm in Bellevue.'),
        'resy.com':      makeEntry('dining', 'dine', 'R', '#000000', '#333333',
            'Find me a trendy dinner reservation for 2 this Saturday at 8pm.')
    };

    function makeEntry(domain, chip, letter, colorA, colorB, defaultPrompt) {
        return {
            domain,
            chip,
            favicon: { letter, color: colorA },
            hero: { gradient: `linear-gradient(135deg, ${colorA} 0%, ${colorB} 100%)` },
            defaultPrompt
        };
    }

    // ------------------------------------------------------------------
    // Starter tabs
    // ------------------------------------------------------------------
    const STARTER_TABS = [
        { url: 'https://www.doordash.com/store/panda-express', title: 'DoorDash — order food' },
        { url: 'https://www.amazon.com/s?k=running+shoes',      title: 'Amazon — running shoes' },
        { url: 'https://www.expedia.com/Flights',               title: 'Expedia — flights' }
    ];

    // ------------------------------------------------------------------
    // DOM
    // ------------------------------------------------------------------
    const $ = (id) => document.getElementById(id);
    const shellEl    = $('edgeShell');
    const tabsEl     = $('tabs');
    const tabNewBtn  = $('tabNewBtn');
    const omniForm   = $('omniboxForm');
    const omniInput  = $('omniboxInput');
    const presetsBtn = $('presetsBtn');
    const presetPop  = $('presetPopover');
    const reloadBtn  = $('reloadBtn');
    const pageEl     = $('page');
    const copilotBtn = $('copilotExtBtn');
    const cpPanel    = $('copilotPanel');
    const cpCloseBtn = $('cpCloseBtn');
    const cpStatus   = $('cpStatus');
    const cpCtxHost  = $('cpCtxHost');
    const cpCtxDom   = $('cpCtxDomain');
    const cpPrompt   = $('cpPromptInput');
    const cpUserId   = $('cpUserIdInput');
    const cpRunBtn   = $('cpRunBtn');
    const cpResults  = $('cpResults');
    const bodyEl     = document.querySelector('.edge-body');

    // ------------------------------------------------------------------
    // State
    // ------------------------------------------------------------------
    const state = {
        tabs: [],           // { id, url, title }
        activeTabId: null,
        sessionId: null,    // last discover sessionId (for Choose/Confirm)
        panelOpen: false
    };
    let tabSeq = 0;

    // ==================================================================
    // Utils
    // ==================================================================
    function normalizeUrl(input) {
        const raw = (input || '').trim();
        if (!raw) return 'about:blank';
        if (/^[a-z]+:\/\//i.test(raw)) return raw;
        if (/^[\w.-]+\.[a-z]{2,}(\/|$)/i.test(raw)) return `https://${raw}`;
        return `https://www.bing.com/search?q=${encodeURIComponent(raw)}`;
    }

    function hostFromUrl(url) {
        try {
            const u = new URL(url);
            return u.hostname.replace(/^www\./i, '').toLowerCase();
        } catch { return ''; }
    }

    function lookupHost(host) {
        if (!host) return null;
        if (HOST_REGISTRY[host]) return { key: host, ...HOST_REGISTRY[host] };
        // Match on parent domain (e.g. shop.amazon.co.uk → amazon.com fallback)
        for (const key of Object.keys(HOST_REGISTRY)) {
            const stem = key.split('.')[0];
            if (host.includes(`.${stem}.`) || host.startsWith(`${stem}.`) || host.endsWith(`.${key}`)) {
                return { key, ...HOST_REGISTRY[key] };
            }
        }
        return null;
    }

    // ==================================================================
    // Tabs
    // ==================================================================
    function makeTab(url, title) {
        return { id: `tab-${++tabSeq}`, url: normalizeUrl(url), title: title || url };
    }

    function activeTab() {
        return state.tabs.find((t) => t.id === state.activeTabId) || null;
    }

    function setActiveTab(id) {
        state.activeTabId = id;
        renderTabs();
        renderOmnibox();
        renderPage();
    }

    function openTab(url, title) {
        const t = makeTab(url, title);
        state.tabs.push(t);
        setActiveTab(t.id);
    }

    function closeTab(id) {
        const idx = state.tabs.findIndex((t) => t.id === id);
        if (idx < 0) return;
        state.tabs.splice(idx, 1);
        if (!state.tabs.length) {
            openTab('about:blank', 'New tab');
            return;
        }
        if (state.activeTabId === id) {
            const next = state.tabs[Math.min(idx, state.tabs.length - 1)];
            setActiveTab(next.id);
        } else {
            renderTabs();
        }
    }

    function renderTabs() {
        tabsEl.innerHTML = '';
        for (const t of state.tabs) {
            const host = hostFromUrl(t.url);
            const entry = lookupHost(host);
            const li = document.createElement('div');
            li.className = 'tab' + (t.id === state.activeTabId ? ' active' : '');
            li.title = t.url;

            const fav = document.createElement('span');
            fav.className = 'tab-favicon';
            const glyph = entry ? entry.favicon : { letter: host ? host[0].toUpperCase() : '·', color: '#83868c' };
            fav.style.background = glyph.color;
            fav.textContent = glyph.letter;

            const title = document.createElement('span');
            title.className = 'tab-title';
            title.textContent = t.title || host || 'New tab';

            const close = document.createElement('button');
            close.type = 'button';
            close.className = 'tab-close';
            close.title = 'Close tab';
            close.innerHTML = '<svg viewBox="0 0 24 24" width="10" height="10" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M6 6l12 12M18 6L6 18"/></svg>';
            close.addEventListener('click', (e) => { e.stopPropagation(); closeTab(t.id); });

            li.appendChild(fav);
            li.appendChild(title);
            li.appendChild(close);
            li.addEventListener('click', () => setActiveTab(t.id));
            tabsEl.appendChild(li);
        }
    }

    function renderOmnibox() {
        const t = activeTab();
        omniInput.value = t ? t.url : '';
    }

    // ==================================================================
    // Simulated page render
    // ==================================================================
    function renderPage() {
        const t = activeTab();
        pageEl.innerHTML = '';
        if (!t) return;
        const host = hostFromUrl(t.url);
        const entry = lookupHost(host);

        const inner = document.createElement('div');
        inner.className = 'page-inner';

        const hero = document.createElement('div');
        hero.className = 'page-hero';
        hero.style.background = entry ? entry.hero.gradient : 'linear-gradient(135deg, #4a5568 0%, #2d3748 100%)';
        const ico = document.createElement('div');
        ico.className = 'ph-icon';
        ico.textContent = entry ? entry.favicon.letter : (host ? host[0].toUpperCase() : '·');
        const heroBody = document.createElement('div');
        const h1 = document.createElement('h1');
        h1.textContent = host || 'new tab';
        const p = document.createElement('p');
        p.textContent = entry
            ? `${capitalize(entry.domain)} site — Copilot has a matching agent for this.`
            : 'Copilot may not have a matching agent for this site.';
        heroBody.appendChild(h1);
        heroBody.appendChild(p);
        hero.appendChild(ico);
        hero.appendChild(heroBody);
        inner.appendChild(hero);

        const grid = document.createElement('div');
        grid.className = 'page-grid';
        const placeholders = mockCards(entry ? entry.domain : 'generic');
        for (const card of placeholders) {
            const c = document.createElement('div');
            c.className = 'page-card';
            const th = document.createElement('div');
            th.className = 'pc-thumb';
            th.textContent = card.thumb;
            const tt = document.createElement('div');
            tt.className = 'pc-title';
            tt.textContent = card.title;
            const mt = document.createElement('div');
            mt.className = 'pc-meta';
            mt.textContent = card.meta;
            c.appendChild(th);
            c.appendChild(tt);
            c.appendChild(mt);
            grid.appendChild(c);
        }
        inner.appendChild(grid);

        const tip = document.createElement('div');
        tip.className = 'page-tip';
        tip.innerHTML = 'Click the <strong>Copilot</strong> button in the toolbar to have an agent do this for you.';
        inner.appendChild(tip);

        pageEl.appendChild(inner);
    }

    function capitalize(s) { return s ? s[0].toUpperCase() + s.slice(1) : s; }

    function mockCards(domain) {
        switch (domain) {
            case 'food delivery': return [
                { thumb: '🍜', title: 'Ramen bowl',      meta: '$12.99 · 25 min' },
                { thumb: '🍕', title: 'Margherita pizza', meta: '$14.99 · 30 min' },
                { thumb: '🥗', title: 'Kale caesar',     meta: '$9.49 · 20 min' },
                { thumb: '🍔', title: 'Cheeseburger',    meta: '$10.99 · 22 min' }
            ];
            case 'shopping': return [
                { thumb: '👟', title: 'Runner Pro 7',    meta: '$79.99' },
                { thumb: '👕', title: 'Cotton tee',      meta: '$14.99' },
                { thumb: '🎧', title: 'Wireless earbuds', meta: '$39.99' },
                { thumb: '⌚', title: 'Smart watch',     meta: '$129.99' }
            ];
            case 'travel': return [
                { thumb: '✈️', title: 'SEA → JFK',       meta: 'From $189, non-stop' },
                { thumb: '🏨', title: 'Hotel Union Sq',  meta: '$220/night' },
                { thumb: '🚗', title: 'Rental — Compact',meta: '$45/day' },
                { thumb: '🎫', title: 'City pass',       meta: '$79' }
            ];
            case 'dining': return [
                { thumb: '🍝', title: 'Trattoria Nova',  meta: 'Italian · 4.6★' },
                { thumb: '🍣', title: 'Kaiseki Kai',     meta: 'Japanese · 4.8★' },
                { thumb: '🥩', title: 'Prime Steakhouse',meta: 'Steakhouse · 4.5★' },
                { thumb: '🍜', title: 'Bone Broth Co.',  meta: 'Ramen · 4.4★' }
            ];
            default: return [
                { thumb: '🔎', title: 'Search result 1', meta: 'sample content' },
                { thumb: '📄', title: 'Search result 2', meta: 'sample content' },
                { thumb: '📄', title: 'Search result 3', meta: 'sample content' }
            ];
        }
    }

    // ==================================================================
    // Copilot panel
    // ==================================================================
    function openPanel() {
        cpPanel.hidden = false;
        bodyEl.classList.add('with-panel');
        state.panelOpen = true;
        copilotBtn.classList.remove('pulse');
    }
    function closePanel() {
        cpPanel.hidden = true;
        bodyEl.classList.remove('with-panel');
        state.panelOpen = false;
    }

    function renderContext(host, entry) {
        cpCtxHost.textContent = host || 'no active page';
        if (!host) {
            cpCtxDom.innerHTML = 'No URL detected in the address bar.';
            return;
        }
        if (!entry) {
            cpCtxDom.innerHTML = `<span class="cp-chip unknown">unknown</span> No matching Copilot agent registered for this host.`;
            return;
        }
        cpCtxDom.innerHTML = `<span class="cp-chip ${entry.chip}">${entry.domain}</span> Copilot will invoke the ${entry.domain} agent for this site.`;
    }

    function clearResults() { cpResults.innerHTML = ''; }

    function showTyping(labelText) {
        clearResults();
        const box = document.createElement('div');
        box.className = 'cp-typing';
        box.innerHTML = `
            <span class="dots"><span></span><span></span><span></span></span>
            <span class="lbl"></span>`;
        box.querySelector('.lbl').textContent = labelText || 'Contacting the orchestrator…';
        cpResults.appendChild(box);
        return box;
    }

    function renderNotFound(host, message) {
        clearResults();
        const box = document.createElement('div');
        box.className = 'cp-not-found';
        box.innerHTML = `
            <strong>Agent not found.</strong>
            <div style="margin-top:6px"></div>`;
        box.lastElementChild.textContent = message ||
            (host
                ? `No Copilot agent is registered for ${host}. Try one of the preset sites via the ⌄ button in the address bar.`
                : 'Open a site in the address bar first, then click the Copilot button.');
        cpResults.appendChild(box);
    }

    function renderError(err) {
        clearResults();
        const box = document.createElement('div');
        box.className = 'cp-error';
        box.textContent = `Request failed: ${err && err.message ? err.message : err}`;
        cpResults.appendChild(box);
    }

    function renderResults(entry, host, prompt, resp) {
        clearResults();

        const summary = document.createElement('div');
        summary.className = 'cp-summary';
        summary.innerHTML = `
            <div><strong>${entry.domain}</strong> agent invoked for <code></code></div>
            <span class="cs-prompt"></span>`;
        summary.querySelector('code').textContent = host;
        summary.querySelector('.cs-prompt').textContent = `“${prompt}”`;
        cpResults.appendChild(summary);

        const options = Array.isArray(resp && resp.options) ? resp.options.slice(0, 3) : [];
        if (!options.length) {
            const box = document.createElement('div');
            box.className = 'cp-not-found';
            box.innerHTML = '<strong>No options returned.</strong>';
            const p = document.createElement('div');
            p.style.marginTop = '6px';
            p.textContent = resp && resp.message ? resp.message : 'The agent did not return any options for this request.';
            box.appendChild(p);
            cpResults.appendChild(box);
            return;
        }

        const list = document.createElement('div');
        list.className = 'cp-options';
        options.forEach((opt, idx) => list.appendChild(renderOptionCard(opt, idx)));
        cpResults.appendChild(list);
    }

    function renderOptionCard(opt, idx) {
        const card = document.createElement('div');
        card.className = 'cp-option';

        const head = document.createElement('div');
        head.className = 'cp-option-head';

        const rank = document.createElement('span');
        rank.className = 'cp-option-rank';
        rank.textContent = String(idx + 1);

        const agent = document.createElement('div');
        agent.className = 'cp-option-agent';
        const agentName = opt.agent_name || opt.agentName || 'Agent';
        const agentType = opt.agent_type || opt.agentType || '';
        agent.textContent = agentType ? `${agentType} · ${agentName}` : agentName;

        head.appendChild(rank);
        head.appendChild(agent);

        const price = opt.price || opt.Price;
        if (price) {
            const p = document.createElement('span');
            p.className = 'cp-option-price';
            p.textContent = price;
            head.appendChild(p);
        }

        const details = document.createElement('div');
        details.className = 'cp-option-details';
        details.textContent = opt.details || opt.Details || '(no details)';

        const actions = document.createElement('div');
        actions.className = 'cp-option-actions';

        const choose = document.createElement('button');
        choose.type = 'button';
        choose.className = 'cp-btn primary';
        choose.textContent = 'Choose';
        choose.addEventListener('click', () => onChoose(opt, choose));

        const confirm = document.createElement('button');
        confirm.type = 'button';
        confirm.className = 'cp-btn';
        confirm.textContent = 'Confirm';
        confirm.addEventListener('click', () => onConfirm(opt, confirm));

        actions.appendChild(choose);
        actions.appendChild(confirm);

        card.appendChild(head);
        card.appendChild(details);
        card.appendChild(actions);
        return card;
    }

    // ==================================================================
    // API
    // ==================================================================
    async function callApi(path, body) {
        const res = await fetch(path, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
            body: JSON.stringify(body)
        });
        let data = null;
        const text = await res.text();
        try { data = text ? JSON.parse(text) : null; } catch { /* keep raw */ }
        if (!res.ok) {
            const errMsg = (data && (data.error || data.detail || data.title)) || text || `HTTP ${res.status}`;
            throw new Error(errMsg);
        }
        return data;
    }

    async function checkHealth() {
        try {
            const res = await fetch('/api/orchestrator/health', { method: 'GET' });
            if (!res.ok) throw new Error('not ok');
            cpStatus.classList.remove('err');
            cpStatus.classList.add('ok');
            cpStatus.querySelector('.status-text').textContent = 'API connected';
        } catch {
            cpStatus.classList.remove('ok');
            cpStatus.classList.add('err');
            cpStatus.querySelector('.status-text').textContent = 'API unreachable';
        }
    }

    // ==================================================================
    // Actions
    // ==================================================================
    async function onCopilotClick() {
        const t = activeTab();
        openPanel();

        const host = t ? hostFromUrl(t.url) : '';
        const entry = lookupHost(host);
        renderContext(host, entry);

        if (!host || !entry) {
            renderNotFound(host);
            return;
        }

        // Prompt: user override wins; otherwise use domain default.
        const userPrompt = cpPrompt.value.trim();
        const prompt = userPrompt || entry.defaultPrompt;
        const userId = cpUserId.value.trim() || 'demo-user';

        const typing = showTyping(`Asking Copilot to invoke the ${entry.domain} agent…`);

        try {
            const resp = await callApi('/api/orchestrator/discover', {
                userId,
                prompt,
                sessionId: state.sessionId,
                // Extra hint fields the API ignores today but that document intent.
                context: { host, siteDomain: entry.domain, source: 'edge-copilot-extension' }
            });
            if (resp && resp.sessionId) state.sessionId = resp.sessionId;
            typing.remove();

            // If the orchestrator says no agent could handle it, show "agent not found".
            if (resp && (resp.status === 'Failed' || (!resp.options || !resp.options.length))) {
                if (resp.status === 'Failed') {
                    renderNotFound(host, resp.message || `Agent not found for ${entry.domain}.`);
                    return;
                }
            }
            renderResults(entry, host, prompt, resp || {});
        } catch (err) {
            renderError(err);
        }
    }

    async function onChoose(option, btn) {
        const optionId = option.option_id || option.optionId;
        if (!state.sessionId || !optionId) {
            alert('Missing session or option id — cannot execute.');
            return;
        }
        btn.disabled = true;
        const oldText = btn.textContent;
        btn.textContent = 'Choosing…';
        try {
            const resp = await callApi('/api/orchestrator/execute', {
                userId: cpUserId.value.trim() || 'demo-user',
                sessionId: state.sessionId,
                optionId,
                prompt: ''
            });
            appendFollowup(resp);
        } catch (err) {
            renderError(err);
        } finally {
            btn.disabled = false;
            btn.textContent = oldText;
        }
    }

    async function onConfirm(option, btn) {
        const optionId = option.option_id || option.optionId;
        if (!state.sessionId || !optionId) {
            alert('Missing session or option id — cannot confirm.');
            return;
        }
        btn.disabled = true;
        const oldText = btn.textContent;
        btn.textContent = 'Confirming…';
        try {
            const resp = await callApi('/api/orchestrator/confirm', {
                userId: cpUserId.value.trim() || 'demo-user',
                sessionId: state.sessionId,
                optionId,
                prompt: 'Please confirm this booking.'
            });
            appendFollowup(resp);
        } catch (err) {
            renderError(err);
        } finally {
            btn.disabled = false;
            btn.textContent = oldText;
        }
    }

    function appendFollowup(resp) {
        const box = document.createElement('div');
        box.className = 'cp-summary';
        const msg = (resp && resp.message) || 'Done.';
        box.innerHTML = `<div><strong>Update:</strong></div><span class="cs-prompt"></span>`;
        box.querySelector('.cs-prompt').textContent = msg;
        cpResults.appendChild(box);
        cpResults.scrollTop = cpResults.scrollHeight;
    }

    // ==================================================================
    // Address bar / presets
    // ==================================================================
    function commitOmnibox() {
        const t = activeTab();
        if (!t) return;
        const url = normalizeUrl(omniInput.value);
        t.url = url;
        const host = hostFromUrl(url);
        const entry = lookupHost(host);
        t.title = entry ? `${host} — ${entry.domain}` : (host || 'New tab');
        state.sessionId = null; // new page = fresh conversation
        renderTabs();
        renderPage();
        // Nudge the user to click Copilot after loading a fresh site.
        copilotBtn.classList.add('pulse');
    }

    function togglePresets() {
        if (!presetPop.hidden) { hidePresets(); return; }
        buildPresetPopover();
        const r = presetsBtn.getBoundingClientRect();
        presetPop.style.top  = `${r.bottom + 6}px`;
        presetPop.style.left = `${Math.max(12, r.right - 260)}px`;
        presetPop.hidden = false;
    }
    function hidePresets() { presetPop.hidden = true; }

    function buildPresetPopover() {
        presetPop.innerHTML = '';
        const groups = new Map();
        for (const [host, entry] of Object.entries(HOST_REGISTRY)) {
            if (!groups.has(entry.domain)) groups.set(entry.domain, []);
            groups.get(entry.domain).push({ host, entry });
        }
        for (const [domain, list] of groups) {
            const lbl = document.createElement('div');
            lbl.className = 'pp-group-label';
            lbl.textContent = domain;
            presetPop.appendChild(lbl);
            for (const { host, entry } of list) {
                const b = document.createElement('button');
                b.type = 'button';
                const fav = document.createElement('span');
                fav.className = 'pp-favicon';
                fav.style.background = entry.favicon.color;
                fav.textContent = entry.favicon.letter;
                b.appendChild(fav);
                const txt = document.createElement('span');
                txt.textContent = host;
                b.appendChild(txt);
                b.addEventListener('click', () => {
                    omniInput.value = `https://www.${host}/`;
                    commitOmnibox();
                    hidePresets();
                });
                presetPop.appendChild(b);
            }
        }
    }

    // ==================================================================
    // Wiring
    // ==================================================================
    omniForm.addEventListener('submit', (e) => { e.preventDefault(); commitOmnibox(); });
    reloadBtn.addEventListener('click', () => { renderPage(); copilotBtn.classList.add('pulse'); });
    tabNewBtn.addEventListener('click', () => openTab('about:blank', 'New tab'));
    copilotBtn.addEventListener('click', onCopilotClick);
    cpCloseBtn.addEventListener('click', closePanel);
    presetsBtn.addEventListener('click', (e) => { e.stopPropagation(); togglePresets(); });
    document.addEventListener('click', (e) => {
        if (!presetPop.hidden && !presetPop.contains(e.target) && e.target !== presetsBtn) {
            hidePresets();
        }
    });
    window.addEventListener('resize', () => { if (!presetPop.hidden) hidePresets(); });

    // ==================================================================
    // Boot
    // ==================================================================
    for (const t of STARTER_TABS) openTab(t.url, t.title);
    setActiveTab(state.tabs[0].id);
    copilotBtn.classList.add('pulse');
    checkHealth();
    setInterval(checkHealth, 20000);
})();
