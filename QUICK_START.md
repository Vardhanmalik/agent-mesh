# Orchestrator Engine - Quick Start Guide

## TL;DR - 5 Minute Setup

### Prerequisites
```bash
# Install required tools
# - .NET 10.0 SDK
# - Azure CLI 2.60+
# - Docker Desktop
# - Git
```

### Step 1: Clone & Setup Resources (2 min)
```bash
# Login to Azure
az login

# Run setup script (creates all Azure resources)
bash azure-setup.sh

# This creates:
# ✓ Resource Group
# ✓ Azure AI Search
# ✓ Azure OpenAI
# ✓ Container Registry
# ✓ Managed Identity
# ✓ Application Insights
# ✓ .env.production file
```

### Step 2: Configure Foundry (1 min)
```bash
# Edit .env.production
# Update these:
AzureFoundry__Endpoint=https://<your-foundry>.services.ai.azure.com/api/projects/<id>
AzureFoundry__ProjectName=<your-project>
AzureFoundry__SubscriptionId=<your-subscription>
AzureFoundry__ResourceGroup=<your-rg>
```

### Step 3: Build & Deploy (2 min)
```bash
# Build and push Docker image
bash build-and-push.sh

# Deploy to App Service
bash deploy-app-service.sh

# Get your app URL
az webapp show -n orchestrator-engine-app -g orchestrator-engine-rg --query defaultHostName
```

### Step 4: Test
```bash
# Test endpoint (wait 2-3 min for container to start)
curl https://<your-app>.azurewebsites.net/healthz

# Test API
curl -X POST https://<your-app>.azurewebsites.net/api/orchestrator/process \
  -H "Content-Type: application/json" \
  -d '{
    "userId": "test@example.com",
    "prompt": "What is Azure?"
  }'
```

---

## What Gets Created

### Azure Resources
| Resource | Purpose | Cost (approx) |
|----------|---------|---------------|
| AI Search (Free tier) | Vector storage for embeddings | $0 |
| OpenAI (S0) | Text embeddings | $8-50/month |
| App Service (B2) | Container hosting | $50-100/month |
| Container Registry (Basic) | Image storage | $5/month |
| Application Insights | Monitoring | Free (5GB/month) |
| Managed Identity | Authentication | Free |
| **Total** | | **$60-160/month** |

### API Endpoints
- `POST /api/orchestrator/process` - Route user to agent
- `POST /api/orchestrator/discover` - Find available agents
- `GET /healthz` - Health check

### Configuration
- **Environment**: Production .NET 10.0
- **Container**: Linux, 1G memory, 1 CPU
- **Auth**: Managed Identity (DefaultAzureCredential)
- **Logging**: Application Insights

---

## Common Operations

### View Logs
```bash
# Real-time logs
az webapp log tail -n orchestrator-engine-app -g orchestrator-engine-rg

# Last 100 lines
az webapp log tail -n orchestrator-engine-app -g orchestrator-engine-rg --tail 100
```

### Restart App
```bash
az webapp restart -n orchestrator-engine-app -g orchestrator-engine-rg
```

### Update Configuration
```bash
# Single setting
az webapp config appsettings set \
  -n orchestrator-engine-app \
  -g orchestrator-engine-rg \
  --settings "AzureFoundry__Endpoint=https://..."

# Multiple settings
az webapp config appsettings set \
  -n orchestrator-engine-app \
  -g orchestrator-engine-rg \
  --settings \
    "Setting1=Value1" \
    "Setting2=Value2"
```

### View Application Insights
```bash
# Get connection string
az monitor app-insights component show \
  --app orchestrator-appinsights \
  -g orchestrator-engine-rg \
  --query connectionString
```

Then visit: https://portal.azure.com → Application Insights → Live Metrics

### Check Resource Status
```bash
# Get app status
az webapp show -n orchestrator-engine-app -g orchestrator-engine-rg

# Get all resources
az resource list -g orchestrator-engine-rg -o table

# Get costs
az costmanagement query \
  --timeframe "MonthToDate" \
  --granularity "Daily"
```

---

## Troubleshooting

### App Won't Start
```bash
# Check if image pulled correctly
az webapp log tail -n orchestrator-engine-app -g orchestrator-engine-rg

# Common issues:
# 1. Wrong registry credentials
# 2. Image not found
# 3. Configuration missing
# 4. Identity doesn't have permissions
```

### Vector Search Errors
```bash
# Check connection string
echo $SEARCH_ENDPOINT
echo $SEARCH_API_KEY

# Test connectivity
curl -X GET "$SEARCH_ENDPOINT/indexes/user-context/stats?api-version=2024-09-01-preview" \
  -H "api-key: $SEARCH_API_KEY"
```

### OpenAI Errors
```bash
# Check deployment
az cognitiveservices account deployment show \
  -n orchestrator-openai \
  -g orchestrator-engine-rg \
  --deployment-id "text-embedding-ada-002"

# Check quota
az cognitiveservices account show \
  -n orchestrator-openai \
  -g orchestrator-engine-rg
```

### Foundry Connection Issues
```bash
# Verify endpoint
echo $AzureFoundry__Endpoint

# Verify credentials
az account show

# Test API
curl -X GET "$AzureFoundry__Endpoint/agents?api-version=2024-12-01-preview" \
  -H "Authorization: Bearer $(az account get-access-token --query accessToken -o tsv)"
```

---

## Next Steps

1. ✅ Run `azure-setup.sh`
2. ✅ Configure Azure Foundry endpoint
3. ✅ Run `build-and-push.sh`
4. ✅ Run `deploy-app-service.sh`
5. ✅ Test endpoints
6. ✅ View logs in Application Insights
7. ✅ Create agents in Azure AI Foundry
8. ✅ Set up monitoring alerts
9. ✅ Enable continuous deployment

---

## Need Help?

See `DEPLOYMENT_GUIDE.md` for complete documentation.

### Quick Links
- [Azure AI Foundry Docs](https://learn.microsoft.com/en-us/azure/ai-services/agents/)
- [Azure Search Docs](https://learn.microsoft.com/en-us/azure/search/)
- [App Service Docs](https://learn.microsoft.com/en-us/azure/app-service/)
- [Azure CLI Reference](https://learn.microsoft.com/en-us/cli/azure/)
