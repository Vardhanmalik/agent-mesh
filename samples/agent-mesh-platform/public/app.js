/* ==========================================================================
   Agent Mesh Platform — Store UI
   - GET /api/orchestrator/agents  → catalog
   - POST /api/orchestrator/onboard → publish agent (wizard step 2)
   ========================================================================== */

(() => {
    'use strict';

    const API_BASE = '/api/orchestrator';
    const DISTRIBUTION_SURFACES = ['Bing', 'MSN', 'Copilot', 'Edge', 'GroupMe'];
    const SURFACE_KEY = { 'Bing': 'bing', 'MSN': 'msn', 'Copilot': 'copilot', 'Edge': 'edge', 'GroupMe': 'groupme', 'Teams': 'teams' };

    const state = {
        agents: [],
        filter: '',
        activeCategory: 'all',
        wizard: {
            step: 1,
            connOk: false
        }
    };

    // ---------- DOM ----------
    const $ = (id) => document.getElementById(id);
    const storeView = $('storeView');
    const detailView = $('detailView');
    const catalogBody = $('catalogBody');
    const searchInput = $('searchInput');
    const filterTabs = $('filterTabs');
    const agentCount = $('agentCount');
    const apiStatus = $('apiStatus');
    const toastStack = $('toastStack');

    const publishModal = $('publishModal');
    const wizardStep1 = $('wizardStep1');
    const wizardStep2 = $('wizardStep2');
    const wizardNextBtn = $('wizardNextBtn');
    const wizardNextLabel = $('wizardNextLabel');
    const wizardNextIcon = $('wizardNextIcon');
    const wizardBackBtn = $('wizardBackBtn');
    const wizardBackLabel = $('wizardBackLabel');
    const stepIndicator = $('stepIndicator');
    const publishTitle = $('publishTitle');
    const publishSub = $('publishSub');
    const stepDots = document.querySelectorAll('.step-dot');
    const testConnBtn = $('testConnBtn');
    const testResult = $('testResult');
    const authorityBlock = $('authorityBlock');

    // ---------- Utilities ----------
    function toast(message, kind = 'info', ttl = 4000) {
        const el = document.createElement('div');
        el.className = `toast ${kind}`;
        el.textContent = message;
        toastStack.appendChild(el);
        setTimeout(() => {
            el.style.opacity = '0';
            el.style.transform = 'translateY(6px)';
            setTimeout(() => el.remove(), 200);
        }, ttl);
    }

    function escapeHtml(str) {
        if (str == null) return '';
        return String(str)
            .replaceAll('&', '&amp;')
            .replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;')
            .replaceAll('"', '&quot;')
            .replaceAll("'", '&#39;');
    }

    function initials(name) {
        if (!name) return 'A';
        const parts = name.trim().split(/\s+/).slice(0, 2);
        return parts.map(p => p[0] || '').join('').toUpperCase() || 'A';
    }

    function avatarGradient(seed) {
        const palettes = [
            'linear-gradient(135deg,#4f6bed,#7c8cf8)',
            'linear-gradient(135deg,#9c6ade,#c69bf1)',
            'linear-gradient(135deg,#e8578c,#f38aa8)',
            'linear-gradient(135deg,#107c41,#4ea86f)',
            'linear-gradient(135deg,#b45309,#e0994b)',
            'linear-gradient(135deg,#0e7490,#4bb3c9)',
            'linear-gradient(135deg,#7c3aed,#a78bfa)',
            'linear-gradient(135deg,#c026d3,#e879f9)'
        ];
        let hash = 0;
        for (const ch of String(seed || '')) hash = ((hash << 5) - hash) + ch.charCodeAt(0);
        return palettes[Math.abs(hash) % palettes.length];
    }

    // ---------- Derived agent fields ----------
    function agentCategory(agent) {
        const cfg = agent.configuration || {};
        const tags = Array.isArray(cfg.tags) ? cfg.tags : [];
        if (tags.length > 0 && tags[0]) return String(tags[0]);
        const caps = Array.isArray(cfg.capabilities) ? cfg.capabilities : (agent.capabilities || []);
        const domainCap = caps.find(c => c && !['mcp', 'code_interpreter', 'file_search', 'function'].includes(c));
        if (domainCap) return String(domainCap);
        return 'General';
    }

    function agentPublisher(agent) {
        const cfg = agent.configuration || {};
        const meta = cfg.metadata || {};
        return meta.publisher || meta.publisher_name || agent.provider || 'Verified publisher';
    }

    function agentCapabilities(agent) {
        const cfg = agent.configuration || {};
        const caps = Array.isArray(cfg.capabilities) && cfg.capabilities.length
            ? cfg.capabilities
            : (agent.capabilities || []);
        return caps.filter(c => c && !['mcp', 'code_interpreter', 'file_search', 'function'].includes(c));
    }

    function categoryLabel(cat) {
        return String(cat || '').replace(/[_-]+/g, ' ').replace(/\b\w/g, m => m.toUpperCase());
    }

    /**
     * Fallback slug builder used when the server didn't send `friendlyId` for an agent (older
     * agents created before the field was added). Mirrors the C# AgentInfo.BuildFriendlyId
     * contract so the same agent renders the same slug on both sides.
     */
    function buildFriendlyId(name) {
        const n = String(name || '').trim().toLowerCase();
        if (!n) return '';
        let slug = '';
        let prevDash = false;
        for (const ch of n) {
            if (/[a-z0-9]/.test(ch)) {
                slug += ch;
                prevDash = false;
            } else if (!prevDash && slug.length) {
                slug += '-';
                prevDash = true;
            }
        }
        if (slug.endsWith('-')) slug = slug.slice(0, -1);
        if (!slug) return '';
        return slug.endsWith('-agent') ? slug : slug + '-agent';
    }

    // ---------- API ----------
    async function apiHealth() {
        try {
            const res = await fetch(`${API_BASE}/health`);
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            apiStatus.classList.add('ok');
            apiStatus.classList.remove('bad');
            apiStatus.querySelector('.status-text').textContent = 'API healthy';
        } catch {
            apiStatus.classList.add('bad');
            apiStatus.classList.remove('ok');
            apiStatus.querySelector('.status-text').textContent = 'API unreachable';
        }
    }

    async function fetchAgents() {
        showLoading();
        try {
            const res = await fetch(`${API_BASE}/agents`);
            if (!res.ok) {
                const body = await res.text().catch(() => '');
                throw new Error(`HTTP ${res.status} — ${body.slice(0, 200)}`);
            }
            const data = await res.json();
            state.agents = Array.isArray(data) ? data : [];
            renderFilterTabs();
            renderCatalog();
        } catch (err) {
            console.error('fetchAgents failed', err);
            renderError(err.message || 'Failed to fetch agents.');
        }
    }

    async function submitOnboard(payload) {
        const res = await fetch(`${API_BASE}/onboard`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        const text = await res.text();
        let json = null;
        try { json = text ? JSON.parse(text) : null; } catch { /* ignore */ }
        if (!res.ok) {
            const detail = (json && (json.error || json.detail)) || text || `HTTP ${res.status}`;
            throw new Error(detail);
        }
        return json;
    }

    // ---------- Rendering ----------
    function showLoading() {
        catalogBody.innerHTML = `
            <div class="empty-state">
                <div class="spinner"></div>
                <p>Fetching agents from the mesh…</p>
            </div>`;
    }

    function renderError(message) {
        catalogBody.innerHTML = `
            <div class="empty-state">
                <div class="empty-title">Couldn't load agents</div>
                <p>${escapeHtml(message)}</p>
                <button class="ghost-btn" id="retryBtn" type="button">Try again</button>
            </div>`;
        $('retryBtn').addEventListener('click', fetchAgents);
    }

    function matchesFilter(agent, needle) {
        if (!needle) return true;
        const cfg = agent.configuration || {};
        const hay = [
            agent.name, agent.description, agent.agentId,
            agentPublisher(agent),
            ...(cfg.tags || []),
            ...(cfg.capabilities || []),
            ...(agent.capabilities || [])
        ].filter(Boolean).join(' ').toLowerCase();
        return hay.includes(needle);
    }

    function renderFilterTabs() {
        const cats = new Set();
        for (const a of state.agents) {
            cats.add(categoryLabel(agentCategory(a)));
        }
        const catList = ['All', ...[...cats].sort((a, b) => a.localeCompare(b))];
        filterTabs.innerHTML = catList.map(c => {
            const key = c.toLowerCase();
            const active = state.activeCategory === (key === 'all' ? 'all' : key);
            return `<button type="button" class="filter-tab ${active ? 'active' : ''}" data-cat="${escapeHtml(key === 'all' ? 'all' : key)}">
                ${escapeHtml(c)}
            </button>`;
        }).join('');
        filterTabs.querySelectorAll('.filter-tab').forEach(btn => {
            btn.addEventListener('click', () => {
                state.activeCategory = btn.getAttribute('data-cat');
                renderFilterTabs();
                renderCatalog();
            });
        });
    }

    function renderCatalog() {
        const needle = state.filter.trim().toLowerCase();
        const visible = state.agents.filter(a => {
            if (!matchesFilter(a, needle)) return false;
            if (state.activeCategory !== 'all') {
                if (categoryLabel(agentCategory(a)).toLowerCase() !== state.activeCategory) return false;
            }
            return true;
        });

        agentCount.textContent = `${visible.length} agent${visible.length === 1 ? '' : 's'}`;

        if (visible.length === 0) {
            const msg = state.agents.length === 0
                ? 'No agents are registered in the mesh yet. Click <strong>Publish an agent</strong> to add one.'
                : `No agents match your filter.`;
            catalogBody.innerHTML = `
                <div class="empty-state">
                    <div class="empty-title">Nothing to show</div>
                    <p>${msg}</p>
                </div>`;
            return;
        }

        catalogBody.innerHTML = visible.map(renderCard).join('');
        catalogBody.querySelectorAll('[data-view-details]').forEach(btn => {
            btn.addEventListener('click', () => {
                const id = btn.getAttribute('data-view-details');
                const agent = state.agents.find(a => a.agentId === id);
                if (agent) openDetail(agent);
            });
        });
    }

    function renderCard(agent) {
        const cat = categoryLabel(agentCategory(agent));
        const publisher = agentPublisher(agent);
        const caps = agentCapabilities(agent).slice(0, 4);
        const capChips = caps.map(c => `<span class="capability-chip">${escapeHtml(c)}</span>`).join('');
        const dist = DISTRIBUTION_SURFACES.map(s => `
            <span class="dist-chip">
                <span class="surface-icon" data-surface="${SURFACE_KEY[s]}"></span>
                ${escapeHtml(s)}
            </span>`).join('');
        const friendlyId = agent.friendlyId || buildFriendlyId(agent.name);

        return `
            <article class="agent-card">
                <div class="card-top">
                    <div class="card-avatar-lg" style="background:${avatarGradient(agent.agentId)}">${escapeHtml(initials(agent.name))}</div>
                    <span class="verified-badge">
                        <svg viewBox="0 0 24 24" width="16" height="16"><circle cx="12" cy="12" r="10" fill="#107c41"/><path d="m8 12 3 3 5-6" stroke="#fff" stroke-width="2" fill="none" stroke-linecap="round" stroke-linejoin="round"/></svg>
                        Verified publisher
                    </span>
                </div>
                <div>
                    <div class="card-category">${escapeHtml(cat)}</div>
                    <div class="card-name">${escapeHtml(agent.name || '(unnamed)')}</div>
                    ${friendlyId ? `<div class="card-friendly-id"><code>${escapeHtml(friendlyId)}</code></div>` : ''}
                    <div class="card-publisher">By ${escapeHtml(publisher)}</div>
                </div>
                <div class="card-description">${escapeHtml(agent.description || 'No description provided.')}</div>
                ${caps.length ? `<div class="card-capabilities">${capChips}</div>` : ''}
                <div class="card-distribution">
                    <div class="distribution-label">Distributed on</div>
                    <div class="distribution-row">${dist}</div>
                </div>
                <div class="card-footer">
                    <button class="primary-btn" type="button" data-view-details="${escapeHtml(agent.agentId)}">View details</button>
                </div>
            </article>`;
    }

    // ---------- Detail view ----------
    function openDetail(agent) {
        const cfg = agent.configuration || {};
        const cat = categoryLabel(agentCategory(agent));
        const publisher = agentPublisher(agent);
        const meta = cfg.metadata || {};
        const publisherDomain = meta.publisher_domain || meta.domain;
        const caps = agentCapabilities(agent);
        // Prefer the server-provided friendlyId; fall back to a name-based slug so older
        // agents (created before the FriendlyId field was added) still show a readable ID.
        const friendlyId = agent.friendlyId || buildFriendlyId(agent.name);

        $('detailHeader').innerHTML = `
            <div class="detail-hero-main">
                <div class="detail-avatar-hero" style="background:${avatarGradient(agent.agentId)}">${escapeHtml(initials(agent.name))}</div>
                <div class="detail-hero-info">
                    <span class="verified-badge">
                        <svg viewBox="0 0 24 24" width="16" height="16"><circle cx="12" cy="12" r="10" fill="#107c41"/><path d="m8 12 3 3 5-6" stroke="#fff" stroke-width="2" fill="none" stroke-linecap="round" stroke-linejoin="round"/></svg>
                        Verified publisher
                    </span>
                    <div class="card-category" style="margin-top:6px">${escapeHtml(cat)}</div>
                    <div class="detail-hero-title">${escapeHtml(agent.name || '(unnamed)')}</div>
                    ${friendlyId ? `<div class="detail-hero-friendly-id"><code>${escapeHtml(friendlyId)}</code></div>` : ''}
                    <div class="detail-hero-desc">${escapeHtml(agent.description || '')}</div>
                    <div class="detail-hero-publisher">
                        By ${escapeHtml(publisher)}
                        ${publisherDomain ? `<span class="dot">·</span> <code>${escapeHtml(publisherDomain)}</code>` : ''}
                    </div>
                </div>
            </div>
            <div class="detail-hero-side">
                <div class="detail-stat-row">
                    <svg viewBox="0 0 24 24" width="16" height="16"><path d="M12 2l3.09 6.26L22 9.27l-5 4.87L18.18 22 12 18.56 5.82 22 7 14.14 2 9.27l6.91-1.01L12 2z"/></svg>
                    New
                </div>
                <div class="detail-stat-label">Simulated installs</div>
            </div>`;

        // Main column
        const mainSections = [];
        if (caps.length) {
            mainSections.push(`
                <div class="detail-card">
                    <h4>Capabilities</h4>
                    <div class="card-capabilities" style="margin-top:8px">
                        ${caps.map(c => `<span class="capability-chip">${escapeHtml(c)}</span>`).join('')}
                    </div>
                </div>`);
        }

        // Try asking - synthesized from the name/description as placeholder examples
        const suggestions = buildTryAsking(agent);
        if (suggestions.length) {
            mainSections.push(`
                <div class="detail-card">
                    <h4>Try asking</h4>
                    <div class="try-asking-list">
                        ${suggestions.map(s => `<div class="try-asking-item">"${escapeHtml(s)}"</div>`).join('')}
                    </div>
                </div>`);
        }

        // Distribution
        mainSections.push(`
            <div class="detail-card">
                <h4>Distribution</h4>
                <div class="distribution-label" style="margin-top:6px">Distributed on</div>
                <div class="distribution-row" style="margin-top:6px">
                    ${DISTRIBUTION_SURFACES.map(s => `
                        <span class="detail-distribution-chip">
                            <span class="surface-icon" data-surface="${SURFACE_KEY[s]}"></span>
                            ${escapeHtml(s)}
                        </span>`).join('')}
                </div>
            </div>`);

        // Surface previews — sample of how the agent appears on each Microsoft surface.
        mainSections.push(renderSurfacePreviews(agent));

        // MCP endpoint details
        const mcps = [
            ['Default MCP', cfg.mcp_server_url],
            ['Discovery',   cfg.mcp_server_url_discovery],
            ['Execution',   cfg.mcp_server_url_execution],
            ['Confirmation',cfg.mcp_server_url_confirmation]
        ].filter(([, url]) => !!url);
        if (mcps.length) {
            mainSections.push(`
                <div class="detail-card">
                    <h4>MCP endpoints</h4>
                    ${mcps.map(([label, url]) => `
                        <div class="mcp-endpoint-row">
                            <span class="mcp-label">${escapeHtml(label)}</span>
                            <code>${escapeHtml(url)}</code>
                        </div>`).join('')}
                </div>`);
        }

        $('detailMain').innerHTML = mainSections.join('');

        // Sidebar
        $('detailSide').innerHTML = `
            <div class="detail-card">
                <div class="identity-eyebrow">One identity advantage</div>
                <div class="identity-icon">
                    <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/></svg>
                </div>
                <div class="identity-title">Microsoft identity, ready everywhere</div>
                <p>With permission, sign-in and familiar details carry across Microsoft surfaces, so customers can continue without starting over.</p>

                <div style="height:14px"></div>
                <div class="identity-eyebrow">Context ready to use</div>
                <ul class="checklist">
                    <li><svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"/></svg> Microsoft account sign-in</li>
                    <li><svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"/></svg> Delivery address</li>
                    <li><svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"/></svg> Occasion preferences</li>
                    <li><svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"/></svg> Contact details</li>
                </ul>

                <div style="height:14px"></div>
                <div class="identity-eyebrow">Customers stay in control</div>
                <p style="margin-top:6px">Sensitive actions require confirmation before the agent proceeds.</p>
                <div style="margin-top:8px">
                    ${caps.slice(0, 2).map(c => `<span class="action-chip">${escapeHtml(c)}</span>`).join('') || '<span class="action-chip">Confirm sensitive action</span>'}
                </div>
            </div>`;

        // Toggle views
        storeView.hidden = true;
        detailView.hidden = false;
        window.scrollTo({ top: 0, behavior: 'instant' });

        // Wire up the surface preview tabs now that the DOM is in place.
        wireSurfacePreviewTabs();
    }

    // ---------- Surface previews (Bing / MSN / Copilot / Edge) ----------
    //
    // The detail view exposes a "Preview across surfaces" card that shows the
    // same agent embedded into each Microsoft surface. Layouts stay constant;
    // per-agent fields (name, publisher, category, sample query, capabilities)
    // plug in wherever the mock references them.

    const PREVIEW_SURFACES = [
        { key: 'copilot', label: 'Copilot' },
        { key: 'bing',    label: 'Bing' },
        { key: 'edge',    label: 'Edge' },
        { key: 'msn',     label: 'MSN' }
    ];

    /** Sample query the previews use \u2014 comes from the first "try_asking" entry when
     *  the agent supplies one, otherwise synthesised from the category. */
    function samplePreviewQuery(agent) {
        const ta = buildTryAsking(agent);
        if (ta.length) return String(ta[0]).replace(/^"+|"+$/g, '');
        const cat = agentCategory(agent);
        return `Show ${String(cat || 'options').toLowerCase()}`;
    }

    function primaryActionLabel(agent) {
        const caps = agentCapabilities(agent);
        return caps[0] || 'Get options';
    }

    function agentInitialsSquare(agent) {
        return `<span class="preview-avatar" style="background:${avatarGradient(agent.agentId)}">${escapeHtml(initials(agent.name))}</span>`;
    }

    function renderSurfacePreviews(agent) {
        const tabs = PREVIEW_SURFACES.map((s, i) => `
            <button type="button" class="preview-tab ${i === 0 ? 'active' : ''}" data-preview-tab="${s.key}" role="tab">
                <span class="surface-icon" data-surface="${s.key}"></span>
                ${escapeHtml(s.label)}
            </button>`).join('');

        const panels = PREVIEW_SURFACES.map((s, i) => `
            <div class="preview-panel ${i === 0 ? 'active' : ''}" data-preview-panel="${s.key}" role="tabpanel">
                ${renderSurfacePreviewPanel(s.key, agent)}
            </div>`).join('');

        return `
            <div class="detail-card preview-card">
                <h4>Preview across surfaces</h4>
                <p style="margin-top:4px">See how ${escapeHtml(agent.name || 'this agent')} appears to users on each Microsoft surface.</p>
                <nav class="preview-tabs" role="tablist">${tabs}</nav>
                <div class="preview-body">${panels}</div>
            </div>`;
    }

    function renderSurfacePreviewPanel(surfaceKey, agent) {
        switch (surfaceKey) {
            case 'copilot': return renderCopilotPreview(agent);
            case 'bing':    return renderBingPreview(agent);
            case 'edge':    return renderEdgePreview(agent);
            case 'msn':     return renderMsnPreview(agent);
            default:        return '';
        }
    }

    function renderCopilotPreview(agent) {
        const query = samplePreviewQuery(agent);
        const publisher = agentPublisher(agent);
        const caps = agentCapabilities(agent).slice(0, 2);
        const capBullets = (caps.length ? caps : ['Get help', 'Review options']).map(c => `
            <div class="preview-cap-row">
                <span class="preview-cap-check">
                    <svg viewBox="0 0 24 24" width="16" height="16"><circle cx="12" cy="12" r="10" fill="#107c41"/><path d="m8 12 3 3 5-6" stroke="#fff" stroke-width="2" fill="none" stroke-linecap="round" stroke-linejoin="round"/></svg>
                </span>
                <div>
                    <div class="preview-cap-title">${escapeHtml(c)}</div>
                    <div class="preview-cap-sub">Available through this agent</div>
                </div>
            </div>`).join('');

        return `
            <div class="mock mock-copilot">
                <div class="mock-copilot-head">
                    <span class="mock-copilot-logo">
                        <svg viewBox="0 0 24 24" width="16" height="16" fill="#7c3aed"><path d="M12 2 4 6v6c0 5 3.4 9.4 8 10 4.6-.6 8-5 8-10V6l-8-4z"/></svg>
                    </span>
                    <span>Microsoft Copilot</span>
                </div>
                <div class="mock-copilot-body">
                    <div class="mock-user-bubble">${escapeHtml(query)}</div>
                    <div class="mock-assist-row">
                        <span class="mock-assist-avatar">
                            <svg viewBox="0 0 24 24" width="14" height="14" fill="#7c3aed"><path d="M12 2 4 6v6c0 5 3.4 9.4 8 10 4.6-.6 8-5 8-10V6l-8-4z"/></svg>
                        </span>
                        <div class="mock-assist-text">Matches ${escapeHtml(String(agentCategory(agent)).toLowerCase())} to your needs and preferences.</div>
                    </div>
                    <div class="mock-agent-card">
                        <div class="mock-agent-eyebrow">SELECTED AGENT \u00b7 ${escapeHtml((agent.name || 'AGENT').toUpperCase())}</div>
                        <div class="mock-agent-head">
                            ${agentInitialsSquare(agent)}
                            <div>
                                <div class="mock-agent-name">${escapeHtml(agent.name || 'Agent')}</div>
                                <div class="mock-agent-publisher">by ${escapeHtml(publisher)}</div>
                            </div>
                        </div>
                        <div class="mock-caps">${capBullets}</div>
                        <div class="mock-actions">
                            <button type="button" class="mock-btn mock-btn-primary">Review options</button>
                            <button type="button" class="mock-btn mock-btn-secondary">Adjust request</button>
                        </div>
                        <div class="mock-consent">Nothing will be completed until you confirm.</div>
                    </div>
                    <div class="mock-footnote">${escapeHtml(agent.name || 'This agent')} was selected for this request based on its registered capabilities.</div>
                </div>
            </div>`;
    }

    function renderBingPreview(agent) {
        const query = samplePreviewQuery(agent);
        const publisher = agentPublisher(agent);
        const cat = String(agentCategory(agent)).toLowerCase();
        const actionLabel = primaryActionLabel(agent);
        return `
            <div class="mock mock-bing">
                <div class="mock-bing-head">
                    <span class="mock-bing-logo">b</span>
                    <div class="mock-bing-search">
                        <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="#6b7280" stroke-width="2" stroke-linecap="round"><circle cx="11" cy="11" r="8"/><path d="m21 21-4.3-4.3"/></svg>
                        <span>${escapeHtml(query)}</span>
                    </div>
                </div>
                <div class="mock-bing-body">
                    <div class="mock-bing-results">
                        <div class="mock-result">
                            <div class="mock-result-url">${escapeHtml(String(publisher || 'publisher').toLowerCase().replace(/\s+/g,''))}.com \u203a ${escapeHtml(cat)}</div>
                            <div class="mock-result-title">${escapeHtml(query)} \u2014 top picks</div>
                            <div class="mock-result-desc">Editorial roundup with detailed reviews, prices, and availability across trusted retailers.</div>
                        </div>
                        <div class="mock-result">
                            <div class="mock-result-url">reviews.example.com \u203a guides</div>
                            <div class="mock-result-title">Buying guide: what to look for</div>
                            <div class="mock-result-desc">A practical checklist of features, tradeoffs, and value picks for ${escapeHtml(cat)}.</div>
                        </div>
                    </div>
                    <aside class="mock-bing-side">
                        <div class="mock-side-eyebrow">SUGGESTED AGENT</div>
                        <div class="mock-agent-head compact">
                            ${agentInitialsSquare(agent)}
                            <div>
                                <div class="mock-agent-name">${escapeHtml(agent.name || 'Agent')}</div>
                                <div class="mock-agent-publisher">by ${escapeHtml(publisher)}</div>
                            </div>
                        </div>
                        <p class="mock-side-desc">${escapeHtml(agent.description || 'Curated recommendations from a verified publisher.')}</p>
                        <button type="button" class="mock-btn mock-btn-primary full">${escapeHtml(actionLabel)}</button>
                    </aside>
                </div>
            </div>`;
    }

    function renderEdgePreview(agent) {
        const query = samplePreviewQuery(agent);
        const publisher = agentPublisher(agent);
        const cat = String(agentCategory(agent)).toLowerCase();
        const actionLabel = primaryActionLabel(agent);
        return `
            <div class="mock mock-edge">
                <div class="mock-edge-chrome">
                    <span class="mock-edge-dots"><i></i><i></i><i></i></span>
                    <div class="mock-edge-url">
                        <svg viewBox="0 0 24 24" width="10" height="10" fill="none" stroke="#6b7280" stroke-width="2"><rect x="5" y="11" width="14" height="10" rx="1"/><path d="M8 11V7a4 4 0 0 1 8 0v4"/></svg>
                        guide.example/${escapeHtml(cat.replace(/\s+/g,'-'))}
                    </div>
                </div>
                <div class="mock-edge-body">
                    <div class="mock-edge-article">
                        <div class="mock-edge-eyebrow">${escapeHtml(cat.toUpperCase())}</div>
                        <div class="mock-edge-title">A practical guide to ${escapeHtml(query.toLowerCase())}</div>
                        <div class="mock-edge-hero"></div>
                        <p class="mock-edge-lede">Review the details, tradeoffs, and recommendations relevant to this page.</p>
                    </div>
                    <aside class="mock-edge-side">
                        <div class="mock-edge-side-head">
                            <svg viewBox="0 0 24 24" width="14" height="14" fill="#7c3aed"><path d="M12 2 4 6v6c0 5 3.4 9.4 8 10 4.6-.6 8-5 8-10V6l-8-4z"/></svg>
                            <span>Copilot</span>
                        </div>
                        <p class="mock-side-desc">You are viewing content about <strong>${escapeHtml(query.toLowerCase())}</strong>. I can ask <strong>${escapeHtml(agent.name || 'the agent')}</strong> to help using this page context.</p>
                        <div class="mock-agent-head compact">
                            ${agentInitialsSquare(agent)}
                            <div>
                                <div class="mock-agent-name">${escapeHtml(agent.name || 'Agent')}</div>
                                <div class="mock-agent-publisher">Verified publisher</div>
                            </div>
                        </div>
                        <button type="button" class="mock-btn mock-btn-primary full">${escapeHtml(actionLabel)}</button>
                        <div class="mock-consent">Shares the current page and your request. Shipping address is available after consent.</div>
                    </aside>
                </div>
            </div>`;
    }

    function renderMsnPreview(agent) {
        const query = samplePreviewQuery(agent);
        const publisher = agentPublisher(agent);
        const cat = String(agentCategory(agent)).toLowerCase();
        const actionLabel = primaryActionLabel(agent);
        return `
            <div class="mock mock-msn">
                <div class="mock-msn-head">
                    <span class="mock-msn-logo">msn</span>
                    <nav class="mock-msn-nav">
                        <span>Discover</span><span>Following</span><span>Watch</span>
                    </nav>
                    <span class="mock-msn-weather">
                        <svg viewBox="0 0 24 24" width="12" height="12" fill="#f2ac21"><circle cx="12" cy="12" r="5"/></svg>
                        72\u00b0
                    </span>
                </div>
                <div class="mock-msn-body">
                    <div class="mock-msn-hero">
                        <div class="mock-msn-hero-img"></div>
                        <div class="mock-msn-hero-eyebrow">${escapeHtml(cat.toUpperCase())}</div>
                        <div class="mock-msn-hero-title">What to know before you ${escapeHtml(query.toLowerCase())}</div>
                    </div>
                    <aside class="mock-msn-side">
                        <div class="mock-msn-side-card recommended">
                            <div class="mock-side-eyebrow">RECOMMENDED</div>
                            <div class="mock-msn-side-title">Useful ideas and comparisons selected for you</div>
                        </div>
                        <div class="mock-msn-side-card">
                            <div class="mock-side-eyebrow">SUGGESTED AGENT \u00b7 VERIFIED PUBLISHER</div>
                            <div class="mock-agent-head compact">
                                ${agentInitialsSquare(agent)}
                                <div>
                                    <div class="mock-agent-name">${escapeHtml(agent.name || 'Agent')}</div>
                                    <div class="mock-agent-publisher">${escapeHtml(publisher)}</div>
                                </div>
                                <span class="mock-heart" aria-hidden="true">
                                    <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="#6b7280" stroke-width="2"><path d="M20.8 4.6a5.5 5.5 0 0 0-7.8 0L12 5.7l-1-1.1a5.5 5.5 0 0 0-7.8 7.8l1 1.1L12 21l7.8-7.5 1-1.1a5.5 5.5 0 0 0 0-7.8z"/></svg>
                                </span>
                            </div>
                            <div class="mock-msn-query">${escapeHtml(query)}</div>
                            <p class="mock-side-desc">${escapeHtml(agent.description || 'Matches ' + cat + ' to your needs.')}</p>
                            <button type="button" class="mock-btn mock-btn-primary">${escapeHtml(actionLabel)}</button>
                        </div>
                    </aside>
                </div>
            </div>`;
    }

    function wireSurfacePreviewTabs() {
        const tabs = document.querySelectorAll('.preview-tab');
        if (!tabs.length) return;
        tabs.forEach(tab => {
            tab.addEventListener('click', () => {
                const key = tab.getAttribute('data-preview-tab');
                document.querySelectorAll('.preview-tab').forEach(t => t.classList.toggle('active', t === tab));
                document.querySelectorAll('.preview-panel').forEach(p => {
                    p.classList.toggle('active', p.getAttribute('data-preview-panel') === key);
                });
            });
        });
    }

    function buildTryAsking(agent) {
        const meta = (agent.configuration || {}).metadata || {};
        const raw = meta.try_asking || meta.tryAsking;
        if (raw) {
            try {
                if (Array.isArray(raw)) return raw.slice(0, 3);
                const parsed = JSON.parse(raw);
                if (Array.isArray(parsed)) return parsed.slice(0, 3);
            } catch { /* fall through */ }
            return String(raw).split('|').map(s => s.trim()).filter(Boolean).slice(0, 3);
        }
        const caps = agentCapabilities(agent);
        if (caps.length === 0) return [];
        return caps.slice(0, 3).map(c => `Help me with ${c.toLowerCase()}.`);
    }

    function goToStore() {
        detailView.hidden = true;
        storeView.hidden = false;
    }

    // ---------- Publish wizard ----------
    function openPublishWizard() {
        state.wizard.step = 1;
        state.wizard.connOk = false;
        testResult.textContent = '';
        testResult.className = 'test-result';
        // reset inputs
        ['fEndpoint', 'fName', 'fPublisher', 'fDescription', 'fCategory', 'fCapabilities', 'fTags', 'fInstructions', 'fAuthority'].forEach(id => {
            const el = $(id); if (el) el.value = '';
        });
        $('fModel').value = 'gpt-4o-mini';
        $('fAuth').value = 'apikey';
        authorityBlock.hidden = true;
        document.querySelector('input[name="connType"][value="mcp"]').checked = true;
        updateWizardChrome();
        publishModal.hidden = false;
        document.body.style.overflow = 'hidden';
        setTimeout(() => $('fEndpoint').focus(), 60);
    }

    function closePublishWizard() {
        publishModal.hidden = true;
        document.body.style.overflow = '';
    }

    function updateWizardChrome() {
        const step = state.wizard.step;
        stepIndicator.textContent = `STEP ${step} OF 2`;
        if (step === 1) {
            publishTitle.textContent = 'Connect the agent';
            publishSub.textContent = 'Provide an MCP or supported agent endpoint. We will infer capabilities after publication.';
            wizardStep1.hidden = false;
            wizardStep2.hidden = true;
            wizardBackLabel.textContent = 'Cancel';
            wizardNextLabel.textContent = 'Continue';
            wizardNextIcon.style.display = '';
            wizardNextBtn.disabled = !$('fEndpoint').value.trim();
        } else {
            publishTitle.textContent = 'Describe the agent';
            publishSub.textContent = 'Give the store the details it needs to route the right agent to the right user.';
            wizardStep1.hidden = true;
            wizardStep2.hidden = false;
            wizardBackLabel.textContent = 'Back';
            wizardNextLabel.textContent = 'Publish agent';
            wizardNextIcon.style.display = 'none';
            wizardNextBtn.disabled = false;
        }
        stepDots.forEach(d => {
            const n = Number(d.getAttribute('data-step'));
            d.classList.toggle('active', n === step);
            d.classList.toggle('done', n < step);
        });
    }

    function splitCsv(value) {
        return String(value || '').split(',').map(s => s.trim()).filter(Boolean);
    }

    async function submitFromWizard() {
        const category = $('fCategory').value.trim();
        const extraTags = splitCsv($('fTags').value);
        const tags = category ? [category, ...extraTags] : extraTags;
        const publisher = $('fPublisher').value.trim();
        const authType = $('fAuth').value;

        const payload = {
            name: $('fName').value.trim(),
            description: $('fDescription').value.trim(),
            model: $('fModel').value.trim() || 'gpt-4o-mini',
            instructions: $('fInstructions').value.trim() || null,
            tags,
            capabilities: splitCsv($('fCapabilities').value),
            authority: authType === 'oauth' ? ($('fAuthority').value.trim() || null) : null,
            mcpServerUrl: $('fEndpoint').value.trim() || null,
            metadata: publisher ? { publisher } : {}
        };

        if (!payload.name || !payload.description) {
            toast('Name and description are required.', 'error');
            return;
        }

        for (const k of Object.keys(payload)) {
            const v = payload[k];
            if (v === null || (Array.isArray(v) && v.length === 0) || (typeof v === 'object' && !Array.isArray(v) && Object.keys(v).length === 0)) {
                delete payload[k];
            }
        }

        wizardNextBtn.disabled = true;
        wizardNextLabel.textContent = 'Publishing…';
        wizardNextBtn.querySelector('.btn-spinner').hidden = false;

        try {
            const created = await submitOnboard(payload);
            const friendly = created?.friendlyId || buildFriendlyId(created?.name || payload.name);
            toast(`Published "${created?.name || payload.name}"${friendly ? ` as ${friendly}` : ''}.`, 'success');
            closePublishWizard();
            await fetchAgents();
        } catch (err) {
            console.error('publish failed', err);
            toast(`Publish failed: ${err.message || err}`, 'error', 6000);
        } finally {
            wizardNextBtn.disabled = false;
            wizardNextLabel.textContent = 'Publish agent';
            wizardNextBtn.querySelector('.btn-spinner').hidden = true;
        }
    }

    // ---------- Event wiring ----------
    $('publishBtn').addEventListener('click', openPublishWizard);
    $('brandHome').addEventListener('click', goToStore);
    $('brandHome').addEventListener('keydown', (e) => { if (e.key === 'Enter') goToStore(); });
    $('backLink').addEventListener('click', (e) => { e.preventDefault(); goToStore(); });

    document.addEventListener('click', (evt) => {
        const closeId = evt.target.closest?.('[data-close-modal]')?.getAttribute('data-close-modal');
        if (closeId) {
            $(closeId).hidden = true;
            document.body.style.overflow = '';
        }
    });
    publishModal.addEventListener('mousedown', (evt) => {
        if (evt.target === publishModal) closePublishWizard();
    });
    document.addEventListener('keydown', (evt) => {
        if (evt.key === 'Escape' && !publishModal.hidden) closePublishWizard();
    });

    // Auth type toggles authority field
    $('fAuth').addEventListener('change', () => {
        authorityBlock.hidden = $('fAuth').value !== 'oauth';
    });

    // Endpoint changes affect Next enablement in step 1
    $('fEndpoint').addEventListener('input', () => {
        if (state.wizard.step === 1) wizardNextBtn.disabled = !$('fEndpoint').value.trim();
        // reset test result on change
        testResult.textContent = '';
        testResult.className = 'test-result';
        state.wizard.connOk = false;
    });

    // Test connection (simulated)
    testConnBtn.addEventListener('click', () => {
        const url = $('fEndpoint').value.trim();
        if (!url) {
            testResult.textContent = 'Enter an endpoint URL first.';
            testResult.className = 'test-result err';
            return;
        }
        testResult.textContent = 'Testing…';
        testResult.className = 'test-result';
        setTimeout(() => {
            if (/fail/i.test(url)) {
                testResult.textContent = 'Connection failed. Check the URL and auth.';
                testResult.className = 'test-result err';
                state.wizard.connOk = false;
            } else {
                testResult.textContent = 'Connected. Handshake OK.';
                testResult.className = 'test-result ok';
                state.wizard.connOk = true;
            }
        }, 700);
    });

    wizardBackBtn.addEventListener('click', () => {
        if (state.wizard.step === 1) {
            closePublishWizard();
        } else {
            state.wizard.step = 1;
            updateWizardChrome();
        }
    });

    wizardNextBtn.addEventListener('click', () => {
        if (state.wizard.step === 1) {
            const endpoint = $('fEndpoint').value.trim();
            if (!endpoint) {
                toast('Endpoint URL is required.', 'error');
                return;
            }
            state.wizard.step = 2;
            updateWizardChrome();
            setTimeout(() => $('fName').focus(), 60);
        } else {
            submitFromWizard();
        }
    });

    // Search
    let searchTimer = null;
    searchInput.addEventListener('input', () => {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(() => {
            state.filter = searchInput.value;
            renderCatalog();
        }, 120);
    });

    // ---------- Boot ----------
    apiHealth();
    fetchAgents();
    setInterval(apiHealth, 30000);
})();
