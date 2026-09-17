# Orchestrator Engine - Testing & Validation Guide

## Pre-Deployment Testing

### 1. Local Development Testing

#### Build Locally
```bash
# Restore dependencies
dotnet restore src/OrchestratorEngine.Api/OrchestratorEngine.Api.csproj

# Build
dotnet build src/OrchestratorEngine.Api/OrchestratorEngine.Api.csproj -c Release

# Run tests (if available)
dotnet test src/OrchestratorEngine.Api/OrchestratorEngine.Api.csproj
```

#### Run Locally with Azure Services
```bash
# Set environment variables
export $(cat .env.production | xargs)

# Run the application
dotnet run --project src/OrchestratorEngine.Api/OrchestratorEngine.Api.csproj \
  --configuration Release \
  --launch-profile https
```

#### Test Local Endpoints
```bash
# Health check
curl http://localhost:5001/healthz

# Process endpoint
curl -X POST http://localhost:5001/api/orchestrator/process \
  -H "Content-Type: application/json" \
  -d '{
    "userId": "test@example.com",
    "prompt": "Test"
  }'

# Discover endpoint
curl -X POST http://localhost:5001/api/orchestrator/discover \
  -H "Content-Type: application/json" \
  -d '{
    "userId": "test@example.com",
    "prompt": "Find agents"
  }'
```

### 2. Docker Testing

#### Build Docker Image
```bash
# Build
docker build -t orchestrator-engine:local -f Dockerfile .

# Verify
docker image ls | grep orchestrator
```

#### Run Container Locally
```bash
# Run with environment file
docker run \
  --name orchestrator-test \
  --env-file .env.production \
  -p 8080:8080 \
  -p 8081:8081 \
  orchestrator-engine:local

# Or use docker-compose
docker-compose -f docker-compose.production.yml up
```

#### Container Testing
```bash
# Health check
curl http://localhost:8080/healthz

# View logs
docker logs orchestrator-test -f

# Enter container
docker exec -it orchestrator-test /bin/bash

# Stop container
docker stop orchestrator-test
docker rm orchestrator-test
```

### 3. Azure Connectivity Testing

#### Test Vector Storage Connection
```bash
#!/bin/bash
SEARCH_ENDPOINT="https://<your-search>.search.windows.net"
SEARCH_API_KEY="<your-api-key>"

# List indexes
curl -X GET "$SEARCH_ENDPOINT/indexes?api-version=2024-09-01-preview" \
  -H "api-key: $SEARCH_API_KEY"

# Check specific index
curl -X GET "$SEARCH_ENDPOINT/indexes/user-context/stats?api-version=2024-09-01-preview" \
  -H "api-key: $SEARCH_API_KEY"

# Test document upload
curl -X POST "$SEARCH_ENDPOINT/indexes/user-context/docs/search?api-version=2024-09-01-preview" \
  -H "Content-Type: application/json" \
  -H "api-key: $SEARCH_API_KEY" \
  -d '{
    "search": "*",
    "count": true
  }'
```

#### Test OpenAI Connection
```bash
#!/bin/bash
OPENAI_ENDPOINT="https://<your-openai>.openai.azure.com/"
OPENAI_KEY="<your-api-key>"

# Get deployment details
curl -X GET "$OPENAI_ENDPOINT/deployments" \
  -H "api-key: $OPENAI_KEY"

# Test embedding
curl -X POST "$OPENAI_ENDPOINT/deployments/text-embedding-ada-002/embeddings?api-version=2024-02-15-preview" \
  -H "Content-Type: application/json" \
  -H "api-key: $OPENAI_KEY" \
  -d '{
    "input": "test"
  }'
```

#### Test Azure Foundry Connection
```bash
#!/bin/bash
FOUNDRY_ENDPOINT="https://<your-foundry>.services.ai.azure.com/api/projects/<id>"
TOKEN=$(az account get-access-token --query accessToken -o tsv)

# List agents
curl -X GET "$FOUNDRY_ENDPOINT/agents?api-version=2024-12-01-preview" \
  -H "Authorization: Bearer $TOKEN"

# Get specific agent
curl -X GET "$FOUNDRY_ENDPOINT/agents/<agent-id>?api-version=2024-12-01-preview" \
  -H "Authorization: Bearer $TOKEN"
```

#### Test Managed Identity
```bash
# Verify identity can access services
az identity show \
  --name orchestrator-engine-identity \
  -g orchestrator-engine-rg

# Check role assignments
az role assignment list \
  --assignee $(az identity show \
    --name orchestrator-engine-identity \
    -g orchestrator-engine-rg \
    --query clientId -o tsv) \
  --output table

# Test Azure CLI with identity
az account show
```

---

## Post-Deployment Testing

### 1. Azure App Service Testing

#### Health Checks
```bash
APP_URL="https://orchestrator-engine-app.azurewebsites.net"

# Health endpoint
curl -v "$APP_URL/healthz"

# OpenAPI spec
curl "$APP_URL/openapi/v1.json" | jq .
```

#### Endpoint Testing
```bash
APP_URL="https://orchestrator-engine-app.azurewebsites.net"

# Process endpoint
curl -X POST "$APP_URL/api/orchestrator/process" \
  -H "Content-Type: application/json" \
  -d '{
    "userId": "test@contoso.com",
    "prompt": "How do I create an Azure storage account?"
  }' | jq .

# Discover endpoint
curl -X POST "$APP_URL/api/orchestrator/discover" \
  -H "Content-Type: application/json" \
  -d '{
    "userId": "test@contoso.com",
    "prompt": "Find infrastructure agents"
  }' | jq .
```

#### Performance Testing
```bash
# Simple load test with 10 concurrent requests
for i in {1..10}; do
  curl -X POST "$APP_URL/api/orchestrator/process" \
    -H "Content-Type: application/json" \
    -d '{
      "userId": "user-'$i'@contoso.com",
      "prompt": "Test"
    }' &
done
wait

# Monitor with ApacheBench
ab -n 100 -c 10 "$APP_URL/healthz"

# Or use vegeta for more detailed metrics
echo "GET $APP_URL/healthz" | vegeta attack -duration=30s | vegeta report
```

#### Logs Testing
```bash
# Real-time logs
az webapp log tail \
  -n orchestrator-engine-app \
  -g orchestrator-engine-rg

# Historical logs (last 100 lines)
az webapp log tail \
  -n orchestrator-engine-app \
  -g orchestrator-engine-rg \
  --tail 100

# Download log files
az webapp log download \
  -n orchestrator-engine-app \
  -g orchestrator-engine-rg
```

### 2. Application Insights Testing

#### Query Metrics
```bash
# Get connection string
APP_INSIGHTS_KEY=$(az monitor app-insights component show \
  --app orchestrator-appinsights \
  -g orchestrator-engine-rg \
  --query instrumentationKey -o tsv)

# Query successful requests
az monitor app-insights query \
  --app orchestrator-appinsights \
  -g orchestrator-engine-rg \
  --analytics-query 'requests | where success == true | project timestamp, name, duration'

# Query failed requests
az monitor app-insights query \
  --app orchestrator-appinsights \
  -g orchestrator-engine-rg \
  --analytics-query 'requests | where success == false | project timestamp, name, resultCode'

# Query custom events
az monitor app-insights query \
  --app orchestrator-appinsights \
  -g orchestrator-engine-rg \
  --analytics-query 'customEvents | project timestamp, name'

# Query dependencies (external calls)
az monitor app-insights query \
  --app orchestrator-appinsights \
  -g orchestrator-engine-rg \
  --analytics-query 'dependencies | project timestamp, type, target, duration'
```

### 3. Resource Health Testing

```bash
# Check all resources
az resource list \
  -g orchestrator-engine-rg \
  --output table

# Check App Service
az webapp show \
  -n orchestrator-engine-app \
  -g orchestrator-engine-rg \
  --query "{state:state, defaultHostName:defaultHostName, location:location}"

# Check Search Service
az search service show \
  -n orchestrator-search \
  -g orchestrator-engine-rg \
  --query "{state:state, endpoint:endpoint, skuName:sku.name}"

# Check OpenAI
az cognitiveservices account show \
  -n orchestrator-openai \
  -g orchestrator-engine-rg \
  --query "{state:properties.publicNetworkAccess, endpoint:properties.endpoint}"

# Check metrics
az monitor metrics list \
  --resource /subscriptions/*/resourceGroups/orchestrator-engine-rg/providers/Microsoft.Web/sites/orchestrator-engine-app \
  --metric "RequestCount" \
  --interval PT5M
```

---

## Automated Testing

### PowerShell Test Script
```powershell
# test-deployment.ps1

param(
    [string]$AppUrl = "https://orchestrator-engine-app.azurewebsites.net"
)

$TestsPassed = 0
$TestsFailed = 0

function Test-Endpoint {
    param([string]$Name, [string]$Method, [string]$Url, [hashtable]$Body)
    
    try {
        $params = @{
            Uri = $Url
            Method = $Method
            ContentType = "application/json"
            ErrorAction = "Stop"
        }
        
        if ($Body) {
            $params.Body = ($Body | ConvertTo-Json)
        }
        
        $response = Invoke-RestMethod @params
        Write-Host "✓ $Name - PASSED" -ForegroundColor Green
        $script:TestsPassed++
        return $true
    }
    catch {
        Write-Host "✗ $Name - FAILED: $_" -ForegroundColor Red
        $script:TestsFailed++
        return $false
    }
}

Write-Host "Testing Orchestrator Engine Deployment"
Write-Host "========================================"
Write-Host ""

# Test 1: Health Check
Test-Endpoint -Name "Health Check" -Method "GET" `
  -Url "$AppUrl/healthz"

# Test 2: Process Endpoint
Test-Endpoint -Name "Process Endpoint" -Method "POST" `
  -Url "$AppUrl/api/orchestrator/process" `
  -Body @{userId="test@contoso.com"; prompt="Test"}

# Test 3: Discover Endpoint
Test-Endpoint -Name "Discover Endpoint" -Method "POST" `
  -Url "$AppUrl/api/orchestrator/discover" `
  -Body @{userId="test@contoso.com"; prompt="Find agents"}

Write-Host ""
Write-Host "========================================"
Write-Host "Tests Passed: $TestsPassed" -ForegroundColor Green
Write-Host "Tests Failed: $TestsFailed" -ForegroundColor Red
Write-Host "========================================"

if ($TestsFailed -eq 0) {
    exit 0
}
else {
    exit 1
}
```

Run with:
```bash
pwsh test-deployment.ps1 -AppUrl "https://orchestrator-engine-app.azurewebsites.net"
```

### Bash Test Script
```bash
#!/bin/bash
# test-deployment.sh

APP_URL="${1:-https://orchestrator-engine-app.azurewebsites.net}"
TESTS_PASSED=0
TESTS_FAILED=0

test_endpoint() {
    local name="$1"
    local method="$2"
    local url="$3"
    local data="$4"
    
    if [ "$method" = "GET" ]; then
        if curl -s -f "$url" > /dev/null; then
            echo "✓ $name - PASSED"
            ((TESTS_PASSED++))
        else
            echo "✗ $name - FAILED"
            ((TESTS_FAILED++))
        fi
    else
        if curl -s -X $method \
            -H "Content-Type: application/json" \
            -d "$data" \
            "$url" > /dev/null; then
            echo "✓ $name - PASSED"
            ((TESTS_PASSED++))
        else
            echo "✗ $name - FAILED"
            ((TESTS_FAILED++))
        fi
    fi
}

echo "Testing Orchestrator Engine Deployment"
echo "========================================"
echo ""

# Test 1: Health Check
test_endpoint "Health Check" "GET" "$APP_URL/healthz"

# Test 2: Process Endpoint
test_endpoint "Process Endpoint" "POST" "$APP_URL/api/orchestrator/process" \
  '{"userId":"test@contoso.com","prompt":"Test"}'

# Test 3: Discover Endpoint
test_endpoint "Discover Endpoint" "POST" "$APP_URL/api/orchestrator/discover" \
  '{"userId":"test@contoso.com","prompt":"Find agents"}'

echo ""
echo "========================================"
echo "Tests Passed: $TESTS_PASSED"
echo "Tests Failed: $TESTS_FAILED"
echo "========================================"

if [ $TESTS_FAILED -eq 0 ]; then
    exit 0
else
    exit 1
fi
```

Run with:
```bash
bash test-deployment.sh "https://orchestrator-engine-app.azurewebsites.net"
```

---

## Debugging Guide

### Enable Debug Logging
```bash
# Update app settings for verbose logging
az webapp config appsettings set \
  -n orchestrator-engine-app \
  -g orchestrator-engine-rg \
  --settings \
    "Logging__LogLevel__Default=Debug" \
    "Logging__LogLevel__Microsoft.AspNetCore=Debug"

# Restart app
az webapp restart -n orchestrator-engine-app -g orchestrator-engine-rg

# View logs
az webapp log tail -n orchestrator-engine-app -g orchestrator-engine-rg
```

### Common Issues & Solutions

| Issue | Solution |
|-------|----------|
| "401 Unauthorized" on Search API | Verify Search Index Data Contributor role assigned |
| "No agents found" from Foundry | Check Foundry endpoint, verify agents exist in Foundry |
| Vector search fails | Verify index exists, check Search service status |
| Slow response time | Check OpenAI quota, monitor App Service CPU/Memory |
| Connection timeout | Check firewall rules, verify Azure service endpoints |

---

## Monitoring Setup

### Create Azure Monitor Alerts

```bash
# Alert for high error rate
az monitor metrics alert create \
  --name "Orchestrator Error Rate" \
  --resource-group orchestrator-engine-rg \
  --scopes /subscriptions/.../orchestrator-engine-app \
  --condition "avg HTTPFailureRate > 5" \
  --window-size 5m \
  --evaluation-frequency 1m

# Alert for slow responses
az monitor metrics alert create \
  --name "Orchestrator High Latency" \
  --resource-group orchestrator-engine-rg \
  --scopes /subscriptions/.../orchestrator-engine-app \
  --condition "avg ResponseTime > 5000" \
  --window-size 5m \
  --evaluation-frequency 1m
```

---

## Performance Benchmarks

Expected metrics (baseline):

| Metric | Expected | Acceptable |
|--------|----------|------------|
| Health check response | <100ms | <500ms |
| Process endpoint | 500-2000ms | <5000ms |
| Discover endpoint | 300-1500ms | <3000ms |
| Vector search | 100-500ms | <1000ms |
| Embedding generation | 200-800ms | <2000ms |
| Error rate | <0.1% | <1% |

---

## CI/CD Testing

For automated deployments, include:

```yaml
# Example: GitHub Actions
- name: Run Tests
  run: |
    bash test-deployment.sh ${{ secrets.APP_URL }}

- name: Check Health
  run: |
    curl -f ${{ secrets.APP_URL }}/healthz || exit 1

- name: Query Metrics
  run: |
    az monitor app-insights query \
      --app orchestrator-appinsights \
      -g orchestrator-engine-rg \
      --analytics-query 'requests | where success == false'
```

---

## Cleanup After Testing

```bash
# Clean up test data
# (Typically not needed for stateless orchestration service)

# But if needed, reset vector index:
az search index delete \
  --name orchestrator-search \
  --resource-group orchestrator-engine-rg \
  --index-name user-context

# Then re-create on next run via EnsureIndexExistsAsync()
```

---

For more details, see DEPLOYMENT_GUIDE.md and API_REFERENCE.md
