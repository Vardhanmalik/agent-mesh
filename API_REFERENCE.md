# Orchestrator Engine - API Reference

## Base URL
```
http://localhost:8080/api/orchestrator    (local development)
https://<app-name>.azurewebsites.net/api/orchestrator    (Azure App Service)
```

## Authentication
- Uses `DefaultAzureCredential` for Azure services
- No API key required for orchestration endpoints in private deployments
- For public endpoints, add Authentication middleware as needed

---

## Endpoints

### 1. Process Orchestration

Route a user prompt to the most appropriate agent.

**POST** `/process`

**Request**
```json
{
  "userId": "user@example.com",
  "prompt": "How do I create a new Azure storage account?",
  "intent": "infrastructure",
  "context": {
    "domain": "azure-cli",
    "sessionId": "sess-123",
    "previousInteractions": 5
  }
}
```

**Request Fields**
| Field | Type | Required | Description |
|-------|------|----------|-------------|
| userId | string | ✓ | Unique user identifier (email or ID) |
| prompt | string | ✓ | User's question or request |
| intent | string | | Domain/category hint (e.g., "infrastructure", "networking") |
| context | object | | Additional context about the user or session |
| context.domain | string | | Problem domain |
| context.sessionId | string | | Session identifier for correlation |
| context.previousInteractions | number | | Count of previous interactions in session |

**Response (200 OK)**
```json
{
  "orchestrationId": "orch-uuid-12345",
  "selectedAgent": {
    "agentId": "agent-001",
    "name": "Azure Storage Agent",
    "description": "Provides guidance on Azure Storage operations",
    "provider": "azure-foundry",
    "capabilities": [
      "create-storage-account",
      "configure-storage",
      "manage-containers"
    ]
  },
  "options": [
    {
      "id": "opt-1",
      "label": "Create Storage Account",
      "description": "Step-by-step guide to create a new storage account with encryption",
      "recommendedAgent": "agent-001"
    },
    {
      "id": "opt-2",
      "label": "Storage Account Best Practices",
      "description": "Review security and performance best practices",
      "recommendedAgent": "agent-001"
    }
  ],
  "confidence": 0.92,
  "timestamp": "2024-09-12T10:30:45.123Z",
  "userContextStored": true,
  "userContextDomain": "azure-storage",
  "similarContextItems": 3
}
```

**Response Fields**
| Field | Type | Description |
|-------|------|-------------|
| orchestrationId | string | Unique ID for this orchestration request |
| selectedAgent | object | The recommended agent for this prompt |
| selectedAgent.agentId | string | Unique agent identifier |
| selectedAgent.name | string | Human-readable agent name |
| selectedAgent.description | string | What this agent does |
| selectedAgent.provider | string | "azure-foundry" or custom provider |
| selectedAgent.capabilities | array | List of agent capabilities |
| options | array | Alternative options/workflows |
| options[].id | string | Option identifier |
| options[].label | string | Short option name |
| options[].description | string | Detailed description |
| options[].recommendedAgent | string | Agent for this option |
| confidence | number | 0-1, confidence in selection |
| timestamp | string | ISO 8601 timestamp |
| userContextStored | boolean | Whether user context was stored |
| userContextDomain | string | Domain of stored context |
| similarContextItems | number | Count of similar past interactions |

**Error Responses**

**400 Bad Request** - Missing required fields
```json
{
  "error": "UserId is required."
}
```

**400 Bad Request** - Empty prompt
```json
{
  "error": "Prompt is required."
}
```

**500 Internal Server Error** - Service unavailable
```json
{
  "error": "Failed to retrieve agents from Foundry",
  "details": "Connection timeout",
  "traceId": "trace-123"
}
```

**Examples**

*Example 1: Azure Infrastructure Question*
```bash
curl -X POST "http://localhost:8080/api/orchestrator/process" \
  -H "Content-Type: application/json" \
  -d '{
    "userId": "john.doe@contoso.com",
    "prompt": "How do I set up a VNet with private endpoints?",
    "intent": "networking",
    "context": {
      "domain": "azure-networking",
      "sessionId": "sess-456"
    }
  }'
```

*Example 2: Data Analysis Question*
```bash
curl -X POST "http://localhost:8080/api/orchestrator/process" \
  -H "Content-Type: application/json" \
  -d '{
    "userId": "analyst@contoso.com",
    "prompt": "Analyze sales trends from Q1 to Q3",
    "intent": "data-analysis"
  }'
```

---

### 2. Agent Discovery

Discover available agents for a given domain or prompt.

**POST** `/discover`

**Request**
```json
{
  "userId": "user@example.com",
  "prompt": "Find agents for database administration",
  "intent": "data"
}
```

**Request Fields**
| Field | Type | Required | Description |
|-------|------|----------|-------------|
| userId | string | ✓ | User identifier |
| prompt | string | ✓ | Description of what you need |
| intent | string | | Optional intent hint |

**Response (200 OK)**
```json
{
  "orchestrationId": "orch-uuid-67890",
  "discoveredAgents": [
    {
      "agentId": "agent-db-001",
      "name": "SQL Database Administrator",
      "description": "Manages Azure SQL Database operations",
      "provider": "azure-foundry",
      "capabilities": [
        "create-database",
        "performance-tuning",
        "backup-restore",
        "security-config"
      ]
    },
    {
      "agentId": "agent-db-002",
      "name": "Cosmos DB Specialist",
      "description": "Handles Cosmos DB design and optimization",
      "provider": "azure-foundry",
      "capabilities": [
        "design-schema",
        "partition-strategy",
        "global-distribution",
        "cost-optimization"
      ]
    },
    {
      "agentId": "agent-db-003",
      "name": "PostgreSQL Expert",
      "description": "Assists with Azure Database for PostgreSQL",
      "provider": "azure-foundry",
      "capabilities": [
        "setup-server",
        "replication",
        "migration",
        "performance-tuning"
      ]
    }
  ],
  "totalAgents": 3,
  "timestamp": "2024-09-12T10:31:00.000Z"
}
```

**Response Fields**
| Field | Type | Description |
|-------|------|-------------|
| orchestrationId | string | Unique request ID |
| discoveredAgents | array | List of available agents |
| discoveredAgents[].agentId | string | Agent identifier |
| discoveredAgents[].name | string | Agent name |
| discoveredAgents[].description | string | Agent description |
| discoveredAgents[].provider | string | Agent provider |
| discoveredAgents[].capabilities | array | List of capabilities |
| totalAgents | number | Total count of agents |
| timestamp | string | ISO 8601 timestamp |

**Examples**

*Find Infrastructure Agents*
```bash
curl -X POST "http://localhost:8080/api/orchestrator/discover" \
  -H "Content-Type: application/json" \
  -d '{
    "userId": "admin@contoso.com",
    "prompt": "I need help with infrastructure as code",
    "intent": "infrastructure"
  }'
```

*Find Security Agents*
```bash
curl -X POST "http://localhost:8080/api/orchestrator/discover" \
  -H "Content-Type: application/json" \
  -d '{
    "userId": "securityuser@contoso.com",
    "prompt": "Find agents for security compliance and auditing"
  }'
```

---

### 3. Health Check

Check the service health.

**GET** `/healthz`

**Response (200 OK)**
```
OK
```

**Purpose**: Used by load balancers and monitoring systems to verify the service is running.

**Examples**
```bash
curl -v http://localhost:8080/api/orchestrator/healthz

# Response:
# < HTTP/1.1 200 OK
# OK
```

---

## Response Codes

| Code | Meaning | When |
|------|---------|------|
| 200 | OK | Request succeeded |
| 400 | Bad Request | Missing/invalid required fields |
| 401 | Unauthorized | Authentication failed (if enabled) |
| 500 | Internal Server Error | Service error, check logs |
| 503 | Service Unavailable | Foundry or dependencies unavailable |

---

## Request/Response Examples

### Full Workflow Example

**1. Discover available agents**
```bash
curl -X POST "http://localhost:8080/api/orchestrator/discover" \
  -H "Content-Type: application/json" \
  -d '{
    "userId": "alice@company.com",
    "prompt": "I need to create a web application"
  }'
```

Response shows 5 available agents for web app development.

**2. Process a specific request**
```bash
curl -X POST "http://localhost:8080/api/orchestrator/process" \
  -H "Content-Type: application/json" \
  -d '{
    "userId": "alice@company.com",
    "prompt": "Create an ASP.NET Core web API with authentication",
    "intent": "web-development",
    "context": {
      "domain": "dotnet",
      "sessionId": "sess-alice-123"
    }
  }'
```

Response recommends the "ASP.NET Core Expert" agent with high confidence.

**3. Check health**
```bash
curl http://localhost:8080/api/orchestrator/healthz
# Response: 200 OK
```

---

## Rate Limiting

Currently no rate limiting. Implement as needed:
- Per-user limits: Configure in middleware
- Per-IP limits: Use API Management gateway
- Per-agent limits: Configure in Foundry

---

## Monitoring & Observability

### Logged Metrics
- Request processing time
- Vector search latency
- Agent selection confidence
- User context stored/retrieved
- Error counts and types

### Accessing Logs
```bash
# View Application Insights logs
az monitor app-insights query \
  --app orchestrator-appinsights \
  -g orchestrator-engine-rg \
  --analytics-query "requests | limit 10"
```

### Correlation ID
Use `traceId` from error responses to find related logs.

---

## Data Models

### UserEmbeddingDocument (Vector Store)
```
Id:              GUID (unique key)
UserId:          User identifier
Content:         The embedded text
Domain:          Context domain (e.g., "azure-storage", "networking")
CreatedAt:       UTC timestamp
ContentVector:   1536-dimensional float array
```

### AgentInfo
```
AgentId:         Unique agent identifier
Name:            Agent display name
Description:     What the agent does
Provider:        "azure-foundry" or custom
Capabilities:    Array of capability strings
```

### OrchestrationRequest
```
UserId:          User identifier
Prompt:          User's question/request
Intent:          Optional domain hint
Context:         Optional additional context
```

### OrchestrationResponse
```
OrchestrationId: Unique request ID
SelectedAgent:   Recommended AgentInfo
Options:         Alternative workflows
Confidence:      Selection confidence (0-1)
Timestamp:       ISO 8601 timestamp
UserContextStored: Boolean
UserContextDomain: String
SimilarContextItems: Count
```

---

## Best Practices

### Client Implementation
1. Always include `userId` and valid `prompt`
2. Use `intent` when known for better routing
3. Store `orchestrationId` for auditing/debugging
4. Handle timeouts (default 30 seconds)
5. Implement retry logic with exponential backoff

### Security
1. Don't expose endpoint publicly without authentication
2. Add rate limiting in production
3. Validate/sanitize user input
4. Log all API access
5. Use HTTPS in production

### Performance
1. Cache agent lists if they change infrequently
2. Use connection pooling for HTTP clients
3. Implement client-side caching for embeddings
4. Monitor vector search latency
5. Use async/await properly in clients

### Error Handling
```python
# Example client error handling
try:
    response = await client.process_orchestration(request)
    agent = response.selected_agent
    # Use recommended agent
except TimeoutError:
    # Retry with backoff
    await asyncio.sleep(2)
except Exception as e:
    # Log and fallback
    logger.error(f"Orchestration failed: {e}")
```

---

## OpenAPI/Swagger

Available at `/openapi/v1.json` (development only)

Import into Postman, Swagger UI, or other tools for interactive testing.

---

## Support

For issues or questions:
1. Check logs in Application Insights
2. Review this API reference
3. Check DEPLOYMENT_GUIDE.md for troubleshooting
4. Contact your Azure support team
