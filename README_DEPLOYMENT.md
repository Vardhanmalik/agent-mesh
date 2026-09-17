# Orchestrator Engine - Complete Deployment & Operations Guide

## 📋 Documentation Overview

This project includes comprehensive documentation for deploying and operating the Orchestrator Engine on Azure. Choose your starting point:

### Quick Start
- **[QUICK_START.md](QUICK_START.md)** - 5-minute setup guide with essential commands

### Complete Guides
- **[DEPLOYMENT_GUIDE.md](DEPLOYMENT_GUIDE.md)** - Full deployment process with all Azure resources
- **[API_REFERENCE.md](API_REFERENCE.md)** - Complete API documentation with examples
- **[TESTING_GUIDE.md](TESTING_GUIDE.md)** - Pre and post-deployment testing procedures

### Automation Scripts
- **[azure-setup.sh](azure-setup.sh)** - Creates all Azure resources
- **[build-and-push.sh](build-and-push.sh)** - Builds and pushes Docker image
- **[deploy-app-service.sh](deploy-app-service.sh)** - Deploys to Azure App Service
- **[docker-compose.production.yml](docker-compose.production.yml)** - Production Docker Compose

---

## 🏗️ Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    User/Client Application                   │
└────────────────────┬────────────────────────────────────────┘
                     │
                     ▼
        ┌────────────────────────────┐
        │   Orchestrator Engine      │
        │  (ASP.NET Core 10.0)       │
        │  - Process Orchestration   │
        │  - Agent Discovery         │
        │  - User Context Management │
        └────────┬────────────────────┘
                 │
        ┌────────┴─────────────────────────────┐
        │                                       │
        ▼                                       ▼
┌──────────────────────┐         ┌──────────────────────┐
│  Azure AI Search     │         │  Azure OpenAI        │
│  (Vector Storage)    │         │  (Embeddings)        │
│                      │         │                      │
│  - User Embeddings   │         │  - text-embedding-   │
│  - Context Storage   │         │    ada-002           │
│  - Similarity Search │         │  - 1536 dimensions   │
└──────────────────────┘         └──────────────────────┘
        │                                 │
        └────────────────────┬────────────┘
                             ▼
                    ┌────────────────────┐
                    │ Azure AI Foundry   │
                    │ (Agent Selection)  │
                    │                    │
                    │ - Agent Management │
                    │ - LLM Routing      │
                    └────────────────────┘
```

---

## 🚀 Quick Start (5 Minutes)

### Prerequisites
```bash
# Ensure you have:
- Azure CLI 2.60+
- .NET 10.0 SDK
- Docker Desktop
- Azure subscription with permissions
```

### Setup Steps

**1. Create Azure Resources**
```bash
bash azure-setup.sh
```
This creates:
- Azure AI Search
- Azure OpenAI with embedding model
- Container Registry
- Managed Identity
- Application Insights

**2. Configure Foundry**
```bash
# Edit .env.production and set:
AzureFoundry__Endpoint=<your-foundry-endpoint>
AzureFoundry__ProjectName=<your-project>
```

**3. Build & Deploy**
```bash
bash build-and-push.sh
bash deploy-app-service.sh
```

**4. Test**
```bash
curl https://<your-app>.azurewebsites.net/healthz
```

See [QUICK_START.md](QUICK_START.md) for detailed instructions.

---

## 📚 Comprehensive Deployment Guide

### Step-by-Step Process

1. **[Create Resource Group & Base Resources](DEPLOYMENT_GUIDE.md#step-1-create-resource-group)**
   - Define location and naming
   - Create resource group

2. **[Create Azure AI Search](DEPLOYMENT_GUIDE.md#step-2-create-azure-ai-search)**
   - Vector storage for embeddings
   - HNSW index configuration

3. **[Create Azure OpenAI](DEPLOYMENT_GUIDE.md#step-3-create-azure-openai)**
   - text-embedding-ada-002 deployment
   - 1536-dimensional embeddings

4. **[Create Azure AI Foundry](DEPLOYMENT_GUIDE.md#step-5-create-azure-ai-foundry-project)**
   - Agent management and routing
   - LLM-based selection

5. **[Create Container Registry](DEPLOYMENT_GUIDE.md#step-6-create-container-registry)**
   - Store Docker images
   - RBAC management

6. **[Set Up Managed Identity](DEPLOYMENT_GUIDE.md#step-7-create-managed-identity)**
   - Secure Azure authentication
   - RBAC role assignments

7. **[Create Monitoring](DEPLOYMENT_GUIDE.md#step-9-create-application-insights)**
   - Application Insights logging
   - Performance monitoring

### Vector Index Configuration

The application automatically creates and maintains the vector index:

**Index**: `user-context`

**Fields**:
```
- Id (string, key)
- UserId (string, filterable)
- Content (string, searchable)
- Domain (string, filterable)
- CreatedAt (DateTimeOffset, sortable)
- ContentVector (1536-dimensional, HNSW)
```

**Vector Algorithm**: HNSW (Hierarchical Navigable Small World)
**Similarity Metric**: Cosine
**Dimensions**: 1536

See [DEPLOYMENT_GUIDE.md#database--vector-index-setup](DEPLOYMENT_GUIDE.md#database--vector-index-setup) for details.

---

## 🔌 API Endpoints

### Base URL
```
http://localhost:8080           (local development)
https://<app>.azurewebsites.net (Azure)
```

### Main Endpoints

**POST** `/api/orchestrator/process`
Route user prompt to appropriate agent with context storage.

**POST** `/api/orchestrator/discover`
Find available agents for a given domain.

**GET** `/healthz`
Health check endpoint.

See [API_REFERENCE.md](API_REFERENCE.md) for complete documentation with request/response examples.

---

## 🔧 Configuration

### Environment Variables

```bash
# Azure Foundry
AzureFoundry__Endpoint          # Project endpoint URL
AzureFoundry__ProjectName       # Project name
AzureFoundry__SubscriptionId    # Azure subscription ID
AzureFoundry__ResourceGroup     # Resource group name
AzureFoundry__SelectorAgentId   # Optional selector agent

# Vector Storage
VectorStorage__Endpoint         # Azure Search URL
VectorStorage__IndexName        # Index name (default: user-context)
VectorStorage__EmbeddingDimensions  # 1536

# Embeddings
Embedding__DeploymentName       # text-embedding-ada-002
Embedding__Endpoint             # Azure OpenAI endpoint

# Monitoring
APPLICATIONINSIGHTS_CONNECTION_STRING
```

See [appsettings.json](src/OrchestratorEngine.Api/appsettings.json) for complete configuration.

---

## 🐳 Docker & Container

### Build Docker Image

```bash
# Local build
docker build -t orchestrator-engine:latest .

# Build and push to ACR
az acr build \
  --registry <registry-name> \
  --image orchestrator-engine:latest \
  --file Dockerfile .
```

### Run in Docker

```bash
# Local with environment file
docker-compose -f docker-compose.production.yml up

# Or manually
docker run \
  --env-file .env.production \
  -p 8080:8080 \
  -p 8081:8081 \
  orchestrator-engine:latest
```

### Dockerfile
The provided [Dockerfile](Dockerfile):
- Uses multi-stage build (SDK → Runtime)
- .NET 10.0 runtime
- Non-root user execution
- Health check enabled
- Optimized for security

---

## 🚁 Deployment Options

### Option 1: Azure Container Instances (Dev/Test)
Simplest option for development.

```bash
az container create \
  --image $IMAGE_URL \
  --resource-group $RESOURCE_GROUP \
  --assign-identity $IDENTITY_ID
```

Cost: ~$40-60/month

### Option 2: Azure App Service (Recommended for Production)
Best for production workloads.

```bash
bash deploy-app-service.sh
```

Cost: ~$50-100/month (B2 SKU)

### Option 3: Azure Kubernetes Service (Enterprise Scale)
For high-availability, multi-replica deployments.

See [DEPLOYMENT_GUIDE.md#option-3-azure-kubernetes-service](DEPLOYMENT_GUIDE.md#option-3-azure-kubernetes-service) for K8s manifests.

Cost: ~$100-500/month

---

## ✅ Testing & Validation

### Pre-Deployment Testing
```bash
# Local build
dotnet build -c Release

# Local run
dotnet run --project src/OrchestratorEngine.Api

# Docker build
docker build -t test:latest .

# Docker run
docker run -p 8080:8080 test:latest
```

### Post-Deployment Testing
```bash
# Health check
curl https://<app>/healthz

# Test API
curl -X POST https://<app>/api/orchestrator/process \
  -H "Content-Type: application/json" \
  -d '{
    "userId": "test@example.com",
    "prompt": "Test"
  }'
```

### Automated Testing
```bash
# Run test suite
bash test-deployment.sh "https://<app>.azurewebsites.net"

# Monitor performance
ab -n 100 -c 10 https://<app>/healthz
```

See [TESTING_GUIDE.md](TESTING_GUIDE.md) for comprehensive testing procedures.

---

## 📊 Monitoring & Logging

### Application Insights
Automatically logs to Application Insights. View metrics:

```bash
# Query errors
az monitor app-insights query \
  --app orchestrator-appinsights \
  --analytics-query 'requests | where success == false'

# Query performance
az monitor app-insights query \
  --app orchestrator-appinsights \
  --analytics-query 'requests | summarize avg(duration), max(duration)'

# View custom events
az monitor app-insights query \
  --app orchestrator-appinsights \
  --analytics-query 'customEvents'
```

### Key Metrics
- Request count & latency
- Error rate & stack traces
- Vector search performance
- Embedding generation time
- Agent selection confidence

### Real-Time Dashboard
Visit Azure Portal → Application Insights → Live Metrics Stream

---

## 🔐 Security

### Authentication
- Uses `DefaultAzureCredential` (Managed Identity)
- No hardcoded API keys
- RBAC-based access control

### Encryption
- HTTPS in production
- Azure Search encryption at rest
- All credentials in Azure Key Vault (recommended)

### Network
- Private endpoints (optional)
- Network Security Groups
- Application Gateway WAF (recommended)

### Best Practices
1. Never commit secrets to repository
2. Use Managed Identity for all Azure services
3. Implement rate limiting
4. Enable Azure Defender
5. Regular security audits

---

## 💰 Cost Estimation

| Resource | SKU | Cost/Month |
|----------|-----|-----------|
| AI Search | Free (dev) / Standard (prod) | $0 / $250+ |
| OpenAI | S0 | $8-50 |
| App Service | B2 | $50-100 |
| Container Registry | Basic | $5 |
| Application Insights | Free tier | $0 |
| Managed Identity | N/A | Free |
| **Total** | | **$60-405/month** |

Optimize by:
- Use Free tier for Search in dev
- Monitor OpenAI usage
- Right-size App Service SKU
- Use reserved instances for long-term

---

## 🔄 CI/CD Integration

### GitHub Actions Example
```yaml
name: Deploy Orchestrator Engine

on:
  push:
    branches: [main]

jobs:
  deploy:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      
      - name: Build Docker Image
        run: |
          docker build -t $REGISTRY/orchestrator:latest .
          
      - name: Push to Registry
        run: |
          docker push $REGISTRY/orchestrator:latest
          
      - name: Deploy to App Service
        run: |
          az webapp deployment container config \
            --name orchestrator-engine-app
          
      - name: Run Tests
        run: bash test-deployment.sh
```

---

## 📖 Documentation Files

| File | Purpose |
|------|---------|
| [QUICK_START.md](QUICK_START.md) | 5-minute setup guide |
| [DEPLOYMENT_GUIDE.md](DEPLOYMENT_GUIDE.md) | Complete deployment process |
| [API_REFERENCE.md](API_REFERENCE.md) | API documentation |
| [TESTING_GUIDE.md](TESTING_GUIDE.md) | Testing procedures |
| [docker-compose.production.yml](docker-compose.production.yml) | Production Docker Compose |
| [Dockerfile](Dockerfile) | Container image definition |
| [appsettings.json](src/OrchestratorEngine.Api/appsettings.json) | Application configuration |

---

## 🆘 Troubleshooting

### Common Issues

**App won't start**
```bash
# View logs
az webapp log tail -n orchestrator-engine-app -g orchestrator-engine-rg

# Check configuration
az webapp config appsettings list -n orchestrator-engine-app -g orchestrator-engine-rg
```

**Vector search fails**
```bash
# Check index exists
curl -X GET "$SEARCH_ENDPOINT/indexes/user-context/stats?api-version=2024-09-01-preview" \
  -H "api-key: $SEARCH_API_KEY"

# Verify role assignment
az role assignment list --assignee $IDENTITY_CLIENT_ID
```

**OpenAI timeouts**
```bash
# Check deployment
az cognitiveservices account deployment show \
  -n orchestrator-openai \
  -g orchestrator-engine-rg \
  --deployment-id "text-embedding-ada-002"

# Monitor quota
az cognitiveservices account show -n orchestrator-openai -g orchestrator-engine-rg
```

See [DEPLOYMENT_GUIDE.md#troubleshooting](DEPLOYMENT_GUIDE.md#troubleshooting) for more solutions.

---

## 🤝 Support & Resources

- **Azure Documentation**: https://learn.microsoft.com/en-us/azure/
- **AI Foundry**: https://learn.microsoft.com/en-us/azure/ai-services/agents/
- **Azure Search**: https://learn.microsoft.com/en-us/azure/search/
- **Azure OpenAI**: https://learn.microsoft.com/en-us/azure/ai-services/openai/
- **ASP.NET Core**: https://learn.microsoft.com/en-us/aspnet/core/
- **.NET 10.0**: https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10

---

## 📋 Deployment Checklist

- [ ] Read QUICK_START.md
- [ ] Run `azure-setup.sh` to create resources
- [ ] Update `.env.production` with Foundry details
- [ ] Run `build-and-push.sh` to build and push image
- [ ] Run `deploy-app-service.sh` to deploy
- [ ] Wait 2-3 minutes for container startup
- [ ] Test health endpoint: `/healthz`
- [ ] Test process endpoint: `POST /api/orchestrator/process`
- [ ] Test discover endpoint: `POST /api/orchestrator/discover`
- [ ] View logs in Application Insights
- [ ] Set up monitoring alerts
- [ ] Configure CI/CD pipeline
- [ ] Create runbooks for operations
- [ ] Document team access procedures
- [ ] Schedule security reviews

---

## 📝 License & Attribution

This project uses:
- .NET 10.0 (MIT License)
- Azure SDK for .NET (MIT License)
- Microsoft.SemanticKernel (MIT License)

See individual package licenses for details.

---

## 🎯 Next Steps

1. **Immediate**: Follow [QUICK_START.md](QUICK_START.md)
2. **Day 1**: Complete deployment using [DEPLOYMENT_GUIDE.md](DEPLOYMENT_GUIDE.md)
3. **Day 1-2**: Run tests from [TESTING_GUIDE.md](TESTING_GUIDE.md)
4. **Week 1**: Set up monitoring and alerts
5. **Week 1-2**: Configure CI/CD and automation
6. **Ongoing**: Monitor logs and metrics

---

**Last Updated**: 2024-09-12
**Version**: 1.0
**Status**: Production Ready
