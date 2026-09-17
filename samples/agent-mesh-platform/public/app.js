/* ==========================================================================
   Agent Mesh Platform — Sample UI
   - Fetches the mesh catalog from GET /api/orchestrator/agents
   - Groups agents by category (derived from tags/capabilities/heuristics)
   - Card click → agent config popup
   - "Onboard Agent" → form → POST /api/orchestrator/onboard → refresh
   ========================================================================== */

(() => {
    'use strict';

    const API_BASE = '/api/orchestrator';
    const state = {
        agents: [],
        filter: ''
    };

    // ---------- DOM ----------
    const $ = (id) => document.getElementById(id);
    const catalogBody = $('catalogBody');
    const searchInput = $('searchInput');
    const apiStatus = $('apiStatus');
    const statAgents = $('statAgents');
    const statCategories = $('statCategories');
    const statSurfaces = $('statSurfaces');
    const toastStack = $('toastStack');

    const onboardModal = $('onboardModal');
    const onboardForm = $('onboardForm');
    const submitBtn = $('submitOnboardBtn');
    const detailModal = $('detailModal');

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

    // Deterministic gradient per agent so category cards read as a set.
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

    // ---------- Category derivation ----------
    // Category priority: first tag → first capability → keyword-scan → surface fallback.
    const CATEGORY_KEYWORDS = [
        ['laptops',  ['laptop', 'gaming', 'notebook', 'macbook', 'dell', 'hp', 'lenovo', 'alienware']],
        ['food',     ['food', 'pizza', 'restaurant', 'delivery', 'grocery', 'ubereats', 'doordash', 'meal']],
        ['travel',   ['flight', 'travel', 'airline', 'hotel', 'booking', 'trip']],
        ['finance',  ['bank', 'finance', 'invest', 'stock', 'trading', 'wallet']],
        ['support',  ['support', 'helpdesk', 'ticket', 'incident', 'triage']],
        ['media',    ['music', 'movie', 'stream', 'video', 'game']],
        ['shopping', ['shop', 'store', 'buy', 'checkout', 'ecommerce', 'retail']]
    ];

    function deriveCategory(agent) {
        const cfg = agent.configuration || {};
        const tags = Array.isArray(cfg.tags) ? cfg.tags : [];
        if (tags.length > 0 && tags[0]) return String(tags[0]).toLowerCase();

        const caps = Array.isArray(cfg.capabilities) ? cfg.capabilities : (agent.capabilities || []);
        // Filter out generic tool types like "mcp" / "code_interpreter"
        const domainCap = caps.find(c => c && !['mcp', 'code_interpreter', 'file_search', 'function'].includes(c));
        if (domainCap) return String(domainCap).toLowerCase();

        const haystack = [agent.name, agent.description, ...(tags || []), ...(caps || [])]
            .filter(Boolean).join(' ').toLowerCase();
        for (const [cat, keys] of CATEGORY_KEYWORDS) {
            if (keys.some(k => haystack.includes(k))) return cat;
        }

        return agent.surface === 'agents-v1' ? 'foundry hosted' : 'general';
    }

    function categoryLabel(cat) {
        return cat.replace(/[_-]+/g, ' ').replace(/\b\w/g, m => m.toUpperCase());
    }

    // ---------- API ----------
    async function apiHealth() {
        try {
            const res = await fetch(`${API_BASE}/health`);
            if (!res.ok) throw new Error(`HTTP ${res.status}`);
            apiStatus.classList.add('ok');
            apiStatus.classList.remove('bad');
            apiStatus.querySelector('.status-text').textContent = 'API healthy';
        } catch (err) {
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
            <div class="empty-state" id="loadingState">
                <div class="spinner"></div>
                <p>Fetching agents from Foundry…</p>
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
            ...(cfg.tags || []),
            ...(cfg.capabilities || []),
            ...(agent.capabilities || [])
        ].filter(Boolean).join(' ').toLowerCase();
        return hay.includes(needle);
    }

    function renderCatalog() {
        const needle = state.filter.trim().toLowerCase();
        const visible = state.agents.filter(a => matchesFilter(a, needle));

        // Stats
        statAgents.textContent = state.agents.length;
        const surfaces = new Set(state.agents.map(a => a.surface).filter(Boolean));
        statSurfaces.textContent = surfaces.size || 0;

        if (visible.length === 0) {
            const emptyMsg = state.agents.length === 0
                ? 'No agents are registered in the mesh yet. Click <strong>Onboard Agent</strong> to add one.'
                : `No agents match "<strong>${escapeHtml(state.filter)}</strong>".`;
            catalogBody.innerHTML = `
                <div class="empty-state">
                    <div class="empty-title">Nothing to show</div>
                    <p>${emptyMsg}</p>
                </div>`;
            statCategories.textContent = state.agents.length === 0 ? '0' : '–';
            return;
        }

        // Group by category
        const groups = new Map();
        for (const a of visible) {
            const cat = deriveCategory(a);
            if (!groups.has(cat)) groups.set(cat, []);
            groups.get(cat).push(a);
        }

        statCategories.textContent = new Set(state.agents.map(deriveCategory)).size;

        // Render categories alphabetically, but keep "general" last.
        const cats = [...groups.keys()].sort((a, b) => {
            if (a === 'general') return 1;
            if (b === 'general') return -1;
            return a.localeCompare(b);
        });

        const html = cats.map(cat => renderCategory(cat, groups.get(cat))).join('');
        catalogBody.innerHTML = html;

        // Wire card clicks
        catalogBody.querySelectorAll('[data-agent-id]').forEach(el => {
            el.addEventListener('click', () => {
                const id = el.getAttribute('data-agent-id');
                const agent = state.agents.find(a => a.agentId === id);
                if (agent) openDetailModal(agent);
            });
        });
    }

    function renderCategory(cat, agents) {
        const cards = agents.map(renderCard).join('');
        return `
            <section class="category-block">
                <div class="category-head">
                    <div class="category-title">${escapeHtml(categoryLabel(cat))}</div>
                    <div class="category-count">${agents.length}</div>
                </div>
                <div class="cards-grid">${cards}</div>
            </section>`;
    }

    function renderCard(agent) {
        const cfg = agent.configuration || {};
        const model = cfg.model ? `<span class="chip brand">${escapeHtml(cfg.model)}</span>` : '';
        const surface = agent.surface
            ? `<span class="chip">${escapeHtml(agent.surface)}</span>` : '';
        const mcp = cfg.mcp_server_url
            ? `<span class="chip success">MCP</span>` : '';
        const tags = Array.isArray(cfg.tags) ? cfg.tags.slice(0, 3) : [];
        const tagChips = tags.map(t => `<span class="chip">${escapeHtml(t)}</span>`).join('');

        return `
            <button class="agent-card" data-agent-id="${escapeHtml(agent.agentId)}" type="button">
                <div class="card-head">
                    <div class="agent-avatar" style="background:${avatarGradient(agent.agentId)}">${escapeHtml(initials(agent.name))}</div>
                    <div style="min-width:0">
                        <div class="card-name">${escapeHtml(agent.name || '(unnamed)')}</div>
                        <div class="card-id" title="${escapeHtml(agent.agentId)}">${escapeHtml(agent.agentId)}</div>
                    </div>
                </div>
                <div class="card-desc">${escapeHtml(agent.description || 'No description provided.')}</div>
                <div class="card-meta">${model}${mcp}${surface}${tagChips}</div>
            </button>`;
    }

    // ---------- Detail modal ----------
    function openDetailModal(agent) {
        const cfg = agent.configuration || {};
        $('detailAvatar').textContent = initials(agent.name);
        $('detailAvatar').style.background = avatarGradient(agent.agentId);
        $('detailAvatar').classList.add('lg');
        $('detailTitle').textContent = agent.name || '(unnamed)';
        $('detailSubtitle').innerHTML = `<code>${escapeHtml(agent.agentId)}</code>`;

        const sections = [];

        // Overview grid
        const overview = [];
        overview.push(kv('Description', agent.description || '—'));
        if (cfg.model) overview.push(kv('Model', cfg.model));
        overview.push(kv('Surface', agent.surface || '—'));
        if (cfg.provider) overview.push(kv('Provider', cfg.provider));
        if (cfg.authority) overview.push(kv('Authority', cfg.authority));
        if (cfg.created_at) overview.push(kv('Created', formatCreatedAt(cfg.created_at)));
        sections.push(`<div class="detail-grid">${overview.join('')}</div>`);

        // Tags / Capabilities
        const chips = [];
        if (Array.isArray(cfg.tags) && cfg.tags.length) {
            chips.push(chipSection('Tags', cfg.tags, 'brand'));
        }
        const caps = Array.isArray(cfg.capabilities) && cfg.capabilities.length
            ? cfg.capabilities
            : (agent.capabilities || []);
        if (caps.length) chips.push(chipSection('Capabilities', caps));
        if (chips.length) sections.push(chips.join(''));

        // MCP endpoints
        const mcps = [
            ['Default', cfg.mcp_server_url],
            ['Discovery', cfg.mcp_server_url_discovery],
            ['Execution', cfg.mcp_server_url_execution],
            ['Confirmation', cfg.mcp_server_url_confirmation]
        ].filter(([, url]) => !!url);
        if (mcps.length) {
            const rows = mcps.map(([label, url]) => `
                <div class="tool-row">
                    <span class="tool-type">${escapeHtml(label)}</span>
                    <code>${escapeHtml(url)}</code>
                </div>`).join('');
            sections.push(`
                <div class="detail-section">
                    <div class="detail-label">MCP Endpoints</div>
                    ${rows}
                </div>`);
        }

        // Tools
        if (Array.isArray(cfg.tools) && cfg.tools.length) {
            const rows = cfg.tools.map(t => `
                <div class="tool-row">
                    <span class="tool-type">${escapeHtml(t.type || 'tool')}</span>
                    ${t.server_label ? `<div>label: <code>${escapeHtml(t.server_label)}</code></div>` : ''}
                    ${t.server_url ? `<div>url: <code>${escapeHtml(t.server_url)}</code></div>` : ''}
                </div>`).join('');
            sections.push(`
                <div class="detail-section">
                    <div class="detail-label">Tools</div>
                    ${rows}
                </div>`);
        }

        // Instructions
        if (cfg.instructions) {
            sections.push(`
                <div class="detail-section">
                    <div class="detail-label">Instructions</div>
                    <div class="detail-value code">${escapeHtml(cfg.instructions)}</div>
                </div>`);
        }

        // Raw metadata
        if (cfg.metadata && typeof cfg.metadata === 'object') {
            const entries = Object.entries(cfg.metadata);
            if (entries.length) {
                const rows = entries.map(([k, v]) =>
                    `<div><strong>${escapeHtml(k)}</strong>: ${escapeHtml(String(v))}</div>`
                ).join('');
                sections.push(`
                    <div class="detail-section">
                        <div class="detail-label">Metadata</div>
                        <div class="detail-value">${rows}</div>
                    </div>`);
            }
        }

        $('detailBody').innerHTML = sections.join('');
        openModal(detailModal);
    }

    function kv(label, value) {
        return `
            <div class="detail-section">
                <div class="detail-label">${escapeHtml(label)}</div>
                <div class="detail-value">${escapeHtml(String(value))}</div>
            </div>`;
    }

    function chipSection(label, items, variant = '') {
        const chips = items.map(i => `<span class="chip ${variant}">${escapeHtml(i)}</span>`).join('');
        return `
            <div class="detail-section">
                <div class="detail-label">${escapeHtml(label)}</div>
                <div class="chip-row">${chips}</div>
            </div>`;
    }

    function formatCreatedAt(raw) {
        if (raw == null) return '—';
        const n = Number(raw);
        if (Number.isFinite(n) && n > 0) {
            // Foundry timestamps are epoch seconds.
            const ms = n < 1e12 ? n * 1000 : n;
            const d = new Date(ms);
            if (!Number.isNaN(d.getTime())) return d.toLocaleString();
        }
        return String(raw);
    }

    // ---------- Modal helpers ----------
    function openModal(el) { el.hidden = false; document.body.style.overflow = 'hidden'; }
    function closeModal(el) { el.hidden = true; document.body.style.overflow = ''; }

    document.addEventListener('click', (evt) => {
        const closeId = evt.target.closest?.('[data-close-modal]')?.getAttribute('data-close-modal');
        if (closeId) closeModal($(closeId));
    });
    // Backdrop click
    [onboardModal, detailModal].forEach(m => {
        m.addEventListener('mousedown', (evt) => {
            if (evt.target === m) closeModal(m);
        });
    });
    document.addEventListener('keydown', (evt) => {
        if (evt.key === 'Escape') {
            if (!onboardModal.hidden) closeModal(onboardModal);
            if (!detailModal.hidden) closeModal(detailModal);
        }
    });

    // ---------- Onboard form ----------
    $('onboardBtn').addEventListener('click', () => {
        onboardForm.reset();
        $('fModel').value = 'gpt-4o-mini';
        openModal(onboardModal);
        setTimeout(() => $('fName').focus(), 50);
    });

    function splitCsv(value) {
        return String(value || '')
            .split(',')
            .map(s => s.trim())
            .filter(Boolean);
    }

    onboardForm.addEventListener('submit', async (evt) => {
        evt.preventDefault();
        const payload = {
            name: $('fName').value.trim(),
            description: $('fDescription').value.trim(),
            model: $('fModel').value.trim() || 'gpt-4o-mini',
            instructions: $('fInstructions').value.trim() || null,
            tags: splitCsv($('fTags').value),
            capabilities: splitCsv($('fCapabilities').value),
            authority: $('fAuthority').value.trim() || null,
            mcpServerUrl: $('fMcpUrl').value.trim() || null,
            mcpServerLabel: $('fMcpLabel').value.trim() || null,
            mcpServerUrlDiscovery: $('fMcpDiscovery').value.trim() || null,
            mcpServerUrlExecution: $('fMcpExecution').value.trim() || null,
            mcpServerUrlConfirmation: $('fMcpConfirmation').value.trim() || null
        };

        // Strip nulls so the server-side model uses its defaults.
        for (const k of Object.keys(payload)) {
            if (payload[k] === null || (Array.isArray(payload[k]) && payload[k].length === 0)) {
                delete payload[k];
            }
        }

        submitBtn.disabled = true;
        submitBtn.querySelector('.btn-label').textContent = 'Onboarding…';
        submitBtn.querySelector('.btn-spinner').hidden = false;

        try {
            const created = await submitOnboard(payload);
            toast(`Onboarded "${created?.name || payload.name}" (${created?.agentId || 'no id'}).`, 'success');
            closeModal(onboardModal);
            await fetchAgents();
        } catch (err) {
            console.error('onboard failed', err);
            toast(`Onboard failed: ${err.message || err}`, 'error', 6000);
        } finally {
            submitBtn.disabled = false;
            submitBtn.querySelector('.btn-label').textContent = 'Onboard agent';
            submitBtn.querySelector('.btn-spinner').hidden = true;
        }
    });

    // ---------- Search + refresh ----------
    let searchTimer = null;
    searchInput.addEventListener('input', () => {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(() => {
            state.filter = searchInput.value;
            renderCatalog();
        }, 120);
    });

    $('refreshBtn').addEventListener('click', () => {
        fetchAgents();
        toast('Refreshing agent list…', 'info', 1500);
    });

    // ---------- Boot ----------
    apiHealth();
    fetchAgents();
    setInterval(apiHealth, 30000);
})();
