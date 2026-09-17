/**
 * Zero-dependency dev server for the Agent Mesh Platform sample UI.
 *
 *  - Serves ./public as static assets on http://localhost:3100
 *  - Proxies /api/* to the OrchestratorEngine API
 *
 * Env vars:
 *   PORT              - port for this dev server         (default 3100)
 *   ORCHESTRATOR_URL  - base URL of the .NET API
 *                       (default http://orchestrator-engine.eastus.azurecontainer.io:8080)
 */

const http = require('http');
const fs = require('fs');
const path = require('path');
const { URL } = require('url');

const PORT = parseInt(process.env.PORT || '3100', 10);
const ORCHESTRATOR_URL = process.env.ORCHESTRATOR_URL
    || 'http://orchestrator-engine.eastus.azurecontainer.io:8080';

const PUBLIC_DIR = path.join(__dirname, 'public');

const MIME_TYPES = {
    '.html': 'text/html; charset=utf-8',
    '.css': 'text/css; charset=utf-8',
    '.js': 'application/javascript; charset=utf-8',
    '.json': 'application/json; charset=utf-8',
    '.svg': 'image/svg+xml',
    '.png': 'image/png',
    '.ico': 'image/x-icon',
    '.woff2': 'font/woff2'
};

function proxyToOrchestrator(req, res) {
    const target = new URL(req.url, ORCHESTRATOR_URL);
    const isHttps = target.protocol === 'https:';
    const client = isHttps ? require('https') : require('http');

    const options = {
        method: req.method,
        hostname: target.hostname,
        port: target.port || (isHttps ? 443 : 80),
        path: target.pathname + target.search,
        headers: { ...req.headers, host: target.host }
    };

    const proxyReq = client.request(options, (proxyRes) => {
        res.writeHead(proxyRes.statusCode || 502, proxyRes.headers);
        proxyRes.pipe(res);
    });

    proxyReq.on('error', (err) => {
        console.error(`[proxy] error contacting ${ORCHESTRATOR_URL}${req.url}:`, err.message);
        res.writeHead(502, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({
            error: 'Orchestrator API unreachable.',
            detail: err.message,
            hint: `Is the API running at ${ORCHESTRATOR_URL}?`
        }));
    });

    req.pipe(proxyReq);
}

function serveStatic(req, res) {
    let urlPath = decodeURIComponent(req.url.split('?')[0]);
    if (urlPath === '/') urlPath = '/index.html';

    const filePath = path.normalize(path.join(PUBLIC_DIR, urlPath));
    if (!filePath.startsWith(PUBLIC_DIR)) {
        res.writeHead(403);
        return res.end('Forbidden');
    }

    fs.stat(filePath, (err, stat) => {
        if (err || !stat.isFile()) {
            res.writeHead(404, { 'Content-Type': 'text/plain' });
            return res.end('Not found');
        }
        const ext = path.extname(filePath).toLowerCase();
        const mime = MIME_TYPES[ext] || 'application/octet-stream';
        res.writeHead(200, {
            'Content-Type': mime,
            'Cache-Control': 'no-store'
        });
        fs.createReadStream(filePath).pipe(res);
    });
}

const server = http.createServer((req, res) => {
    if (req.url && req.url.startsWith('/api/')) {
        return proxyToOrchestrator(req, res);
    }
    serveStatic(req, res);
});

server.listen(PORT, () => {
    console.log('');
    console.log('   Agent Mesh Platform — sample UI');
    console.log('   ─────────────────────────────────────────');
    console.log(`   UI     : http://localhost:${PORT}`);
    console.log(`   API    : ${ORCHESTRATOR_URL}`);
    console.log('');
});
