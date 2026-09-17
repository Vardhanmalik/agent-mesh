# Orchestrator Engine - Complete Azure Deployment Guide

## Table of Contents
1. [Architecture Overview](#architecture-overview)
2. [Prerequisites](#prerequisites)
3. [Azure Resources Setup](#azure-resources-setup)
4. [Configuration](#configuration)
5. [Docker Build & Registry](#docker-build--registry)
6. [Deployment Options](#deployment-options)
7. [Database & Vector Index Setup](#database--vector-index-setup)
8. [Endpoints & API Reference](#endpoints--api-reference)
9. [Monitoring & Logging](#monitoring--logging)
10. [Post-Deployment Validation](#post-deployment-validation)

---

## Architecture Overview

The Orchestrator Engine is an ASP.NET Core 10.0 application that:
- Routes user prompts to Azure AI Foundry agents
- Stores and retrieves user context using Azure AI Search (vector embeddings)
- Generates embeddings via Azure OpenAI
- Integrates with Microsoft WorkIQ for contextual information

### Technology Stack
- **Runtime**: .NET 10.0 (Linux containers)
- **Web Framework**: ASP.NET Core Minimal APIs
- **Vector Database**: Azure AI Search
- **Embeddings**: Azure OpenAI (text-embedding-ada-002, 1536 dimensions)
- **AI Services**: Azure AI Foundry, Microsoft WorkIQ
- **Authentication**: Azure AD (Managed Identity via DefaultAzureCredential)
- **Logging**: Application Insights / Azure Monitor

### Dependencies
```
Azure.AI.Projects                  v2.1.0-beta.4
Azure.Identity                     v1.21.0
Azure.Search.Documents             v12.0.0
Microsoft.SemanticKernel           v1.80.0
Microsoft.AspNetCore.OpenApi       v10.0.11
```

---

## Prerequisites

### Local Requirements
- **.NET 10.0 SDK** or later
- **Docker Desktop** (with Linux containers enabled)
- **Azure CLI 2.60+**
- **Git**

### Azure Requirements
- Active Azure subscription
- Sufficient quota for:
  - Azure AI Search
  - Azure OpenAI
  - Azure AI Foundry
  - Container Registry
  - Container Instances / App Service / AKS

### Permissions
- Azure subscription Contributor or Owner role
- Create resource groups
- Create and manage Azure services

---

## Azure Resources Setup

### Step 1: Create Resource Group

```bash
# Set environment variables
RESOURCE_GROUP="orchestrator-engine-rg"
LOCATION="eastus"  # or your preferred region
SUBSCRIPTION_ID="0685a6ae-234f-45d4-8765-0469aa51ceb0"

# Create resource group
az group create \
  --name $RESOURCE_GROUP \
  --location $LOCATION

# Set as default
az configure --defaults group=$RESOURCE_GROUP
```

### Step 2: Create Azure AI Search

```bash
SEARCH_SERVICE_NAME="orchestrator-searcher"
SEARCH_SKU="standard"  # Use 'free' for dev, 'standard' for prod

az search service create \
  --name $SEARCH_SERVICE_NAME \
  --resource-group $RESOURCE_GROUP \
  --sku $SEARCH_SKU \
  --replica-count 1 \
  --partition-count 1

# Get endpoint
SEARCH_ENDPOINT=$(az search service show \
  --name $SEARCH_SERVICE_NAME \
  --query endpoint -o tsv)

echo "Search Endpoint: $SEARCH_ENDPOINT"
```

### Step 3: Create Azure OpenAI

```bash
OPENAI_NAME="orchestrator-openai"
OPENAI_SKU="S0"

az cognitiveservices account create \
  --name $OPENAI_NAME \
  --resource-group $RESOURCE_GROUP \
  --kind OpenAI \
  --sku $OPENAI_SKU \
  --location $LOCATION

# Get endpoint
OPENAI_ENDPOINT=$(az cognitiveservices account show \
  --name $OPENAI_NAME \
  --query properties.endpoint -o tsv)

echo "OpenAI Endpoint: $OPENAI_ENDPOINT"
```

### Step 4: Deploy Embedding Model

```bash
# Deploy text-embedding-ada-002
az cognitiveservices account deployment create \
  --resource-group $RESOURCE_GROUP \
  --name $OPENAI_NAME \
  --deployment-name "text-embedding-ada-002" \
  --model-name "text-embedding-ada-002" \
  --model-version "2" \
  --model-format "OpenAI" \
  --sku-name "Standard" \
  --sku-capacity 1
```

### Step 5: Create Azure AI Foundry Project

```bash
# Note: AI Foundry is managed via Azure Portal or SDK
# Steps:
# 1. Go to Azure Portal
# 2. Create "AI Project" resource
# 3. Note the Project Name and Project ID
# 4. Create required agents in Foundry

FOUNDRY_PROJECT_NAME="orchestrator-foundry"
FOUNDRY_ENDPOINT="https://orchestrator-foundry.services.ai.azure.com/api/projects"
FOUNDRY_PROJECT_ID="/subscriptions/0685a6ae-234f-45d4-8765-0469aa51ceb0/resourceGroups/orchestrator-engine-rg/providers/Microsoft.CognitiveServices/accounts/orchestrator-foundry/projects/orchestrator-foundry"
FOUNDRY_RESOURCE_GROUP=$RESOURCE_GROUP
FOUNDRY_SUBSCRIPTION_ID=$SUBSCRIPTION_ID
```

### Step 6: Create Container Registry

```bash
REGISTRY_NAME="orchestratorhackathon"  # Must be globally unique

az acr create \
  --resource-group $RESOURCE_GROUP \
  --name $REGISTRY_NAME \
  --sku Basic \
  --admin-enabled true

# Get registry credentials
REGISTRY_URL=$(az acr show \
  --name $REGISTRY_NAME \
  --query loginServer -o tsv)

REGISTRY_USERNAME=$(az acr credential show \
  --name $REGISTRY_NAME \
  --query username -o tsv)

REGISTRY_PASSWORD=$(az acr credential show \
  --name $REGISTRY_NAME \
  --query passwords[0].value -o tsv)

echo "Registry URL: $REGISTRY_URL"
echo "Registry Username: $REGISTRY_USERNAME"
```

### Step 7: Create Managed Identity

```bash
IDENTITY_NAME="orchestrator-engine-identity"

az identity create \
  --resource-group $RESOURCE_GROUP \
  --name $IDENTITY_NAME

IDENTITY_ID=$(az identity show \
  --resource-group $RESOURCE_GROUP \
  --name $IDENTITY_NAME \
  --query id -o tsv)

IDENTITY_CLIENT_ID=$(az identity show \
  --resource-group $RESOURCE_GROUP \
  --name $IDENTITY_NAME \
  --query clientId -o tsv)

echo "Identity ID: $IDENTITY_ID"
echo "Identity Client ID: $IDENTITY_CLIENT_ID"
```

### Step 8: Grant RBAC Permissions to Managed Identity

**Critical:** Azure CLI must have subscription context set. Follow these steps in order:

**Step 8a: Verify and Set Subscription Context**

First, verify you're logged in and check current subscription:
```bash
# Check current Azure login
az account show

# If it returns nothing or wrong subscription, set it explicitly
az account set --subscription 0685a6ae-234f-45d4-8765-0469aa51ceb0
```

**Step 8b: Export All Required Variables**

```bash
export SUBSCRIPTION_ID="0685a6ae-234f-45d4-8765-0469aa51ceb0"
export RESOURCE_GROUP="orchestrator-engine-rg"
export LOCATION="eastus"
export SEARCH_SERVICE_NAME="orchestrator-search"
export OPENAI_NAME="orchestrator-openai"
export REGISTRY_NAME="orchestratorhackathon"
export IDENTITY_NAME="orchestrator-engine-identity"
export APPINSIGHTS_NAME="orchestrator-appinsights"

# Retrieve Identity Client ID (this requires Resources to exist)
export IDENTITY_CLIENT_ID=$(az identity show \
  --resource-group $RESOURCE_GROUP \
  --name $IDENTITY_NAME \
  --query clientId -o tsv \
  --subscription $SUBSCRIPTION_ID)

# Verify it was retrieved
echo "Identity Client ID: $IDENTITY_CLIENT_ID"
```

**Step 8c: Verify Variables Before Creating Role Assignments**

```bash
# These should ALL show values (not empty):
echo "Subscription ID: $SUBSCRIPTION_ID"
echo "Resource Group: $RESOURCE_GROUP"
echo "Search Service: $SEARCH_SERVICE_NAME"
echo "OpenAI Service: $OPENAI_NAME"
echo "Identity Client ID: $IDENTITY_CLIENT_ID"

# If any are empty, STOP and re-run the export commands above
```

**Step 8d: Create the Role Assignments**

Now create the role assignments:

```bash
```bash
# Get the resource IDs (we'll use these in scopes)
SEARCH_ID=$(az search service show \
  --name $SEARCH_SERVICE_NAME \
  --resource-group $RESOURCE_GROUP \
  --query id -o tsv)

OPENAI_ID=$(az cognitiveservices account show \
  --name $OPENAI_NAME \
  --resource-group $RESOURCE_GROUP \
  --query id -o tsv)

echo "Search Resource ID: $SEARCH_ID"
echo "OpenAI Resource ID: $OPENAI_ID"
```

**Then create the role assignments:**

```bash
# Azure AI Search - Search Index Data Reader
az role assignment create \
  --assignee $IDENTITY_CLIENT_ID \
  --role "Search Index Data Reader" \
  --scope $SEARCH_ID

# Azure AI Search - Search Index Data Contributor
az role assignment create \
  --assignee $IDENTITY_CLIENT_ID \
  --role "Search Index Data Contributor" \
  --scope $SEARCH_ID

# Azure OpenAI - Cognitive Services User
az role assignment create \
  --assignee $IDENTITY_CLIENT_ID \
  --role "Cognitive Services User" \
  --scope $OPENAI_ID

# Azure OpenAI - Cognitive Services OpenAI User
az role assignment create \
  --assignee $IDENTITY_CLIENT_ID \
  --role "Cognitive Services OpenAI User" \
  --scope $OPENAI_ID
```

**Verify the role assignments were created:**

```bash
az role assignment list \
  --assignee $IDENTITY_CLIENT_ID \
  --output table
```

You should see 4 role assignments listed.
```

### Step 9: Create Application Insights

```bash
APPINSIGHTS_NAME="orchestrator-appinsights"

az monitor app-insights component create \
  --app $APPINSIGHTS_NAME \
  --location $LOCATION \
  --resource-group $RESOURCE_GROUP \
  --application-type web

APPINSIGHTS_KEY=$(az monitor app-insights component show \
  --app $APPINSIGHTS_NAME \
  --resource-group $RESOURCE_GROUP \
  --query instrumentationKey -o tsv)

echo "App Insights Key: $APPINSIGHTS_KEY"
```

---

## Configuration

### Step 1: Create Configuration File for Production

Create `appsettings.Production.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.Extensions.Http": "Warning"
    },
    "ApplicationInsights": {
      "LogLevel": {
        "Default": "Information"
      }
    }
  },
  "AllowedHosts": "*",
  "AzureFoundry": {
    "Endpoint": "https://<your-foundry-project>.services.ai.azure.com/api/projects/<project-id>",
    "ProjectName": "<your-project-name>",
    "SubscriptionId": "<your-subscription-id>",
    "ResourceGroup": "<your-resource-group>",
    "SelectorAgentId": "<optional-selector-agent-id>"
  },
  "VectorStorage": {
    "Endpoint": "https://<your-search-service>.search.windows.net",
    "IndexName": "user-context",
    "EmbeddingDimensions": 1536
  },
  "Embedding": {
    "DeploymentName": "text-embedding-ada-002",
    "Endpoint": "https://<your-openai-endpoint>.openai.azure.com/"
  },
  "WorkIQ": {
    "Endpoint": "https://<your-workiq-endpoint>",
    "ApiVersion": "2024-12-01-preview",
    "Scope": "https://workiq.microsoft.com/.default",
    "Enabled": true,
    "MaxItemsPerQuery": 10,
    "MaxContentLengthChars": 2000,
    "MinRelevanceScore": 0.5,
    "CacheTtlMinutes": 60,
    "AllowedCategories": [],
    "VectorStorageDomain": "workiq-context"
  }
}
```

### Step 2: Configure Environment Variables for Container

Create `.env.production` for local Docker testing:

```bash
# Azure Resources
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_HTTP_PORTS=8080
ASPNETCORE_HTTPS_PORTS=8081
ASPNETCORE_URLS=http://+:8080;https://+:8081

# Azure Foundry
AzureFoundry__Endpoint=https://<your-foundry-project>.services.ai.azure.com/api/projects/<project-id>
AzureFoundry__ProjectName=<your-project-name>
AzureFoundry__SubscriptionId=<your-subscription-id>
AzureFoundry__ResourceGroup=<your-resource-group>
AzureFoundry__SelectorAgentId=<optional-agent-id>

# Vector Storage (Azure AI Search)
VectorStorage__Endpoint=https://<your-search-service>.search.windows.net
VectorStorage__IndexName=user-context
VectorStorage__EmbeddingDimensions=1536

# Embeddings (Azure OpenAI)
Embedding__DeploymentName=text-embedding-ada-002
Embedding__Endpoint=https://<your-openai-endpoint>.openai.azure.com/

# WorkIQ (Microsoft Graph Context)
WorkIQ__Endpoint=https://<your-workiq-endpoint>
WorkIQ__ApiVersion=2024-12-01-preview
WorkIQ__Scope=https://workiq.microsoft.com/.default
WorkIQ__Enabled=true
WorkIQ__MaxItemsPerQuery=10
WorkIQ__MaxContentLengthChars=2000
WorkIQ__MinRelevanceScore=0.5
WorkIQ__CacheTtlMinutes=60
WorkIQ__VectorStorageDomain=workiq-context

# Application Insights
APPLICATIONINSIGHTS_CONNECTION_STRING=InstrumentationKey=<your-app-insights-key>
```

---

## Docker Build & Registry

### Step 1: Update Dockerfile for Production

The existing Dockerfile is good but add these improvements:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# Security: Create non-root user
RUN groupadd -r appuser && useradd -r -g appuser appuser

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/OrchestratorEngine.Api/OrchestratorEngine.Api.csproj", "src/OrchestratorEngine.Api/"]
RUN dotnet restore "src/OrchestratorEngine.Api/OrchestratorEngine.Api.csproj"
COPY . .
WORKDIR "/src/src/OrchestratorEngine.Api"
RUN dotnet build "OrchestratorEngine.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "OrchestratorEngine.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Health check
HEALTHCHECK --interval=30s --timeout=3s --start-period=40s --retries=3 \
  CMD curl -f http://localhost:8080/healthz || exit 1

# Run as non-root user
USER appuser

ENTRYPOINT ["dotnet", "OrchestratorEngine.Api.dll"]
```

### Step 2: Build and Push to Container Registry

```bash
# Login to ACR
az acr login --name $REGISTRY_NAME

# Build image
az acr build \
  --registry $REGISTRY_NAME \
  --image orchestrator-engine:latest \
  --image orchestrator-engine:1.0.0 \
  --file Dockerfile \
  .

# Verify build
az acr repository show-tags \
  --name $REGISTRY_NAME \
  --repository orchestrator-engine

# Image URL for deployment
IMAGE_URL="$REGISTRY_URL/orchestrator-engine:latest"
echo "Image URL: $IMAGE_URL"
```

### Step 3: Create Docker Compose for Local Testing

Create `docker-compose.production.yml`:

```yaml
services:
  orchestrator-engine:
    image: orchestrator-engine:latest
    container_name: orchestrator-engine-prod
    ports:
      - "8080:8080"
      - "8081:8081"
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_HTTP_PORTS=8080
      - ASPNETCORE_HTTPS_PORTS=8081
      - AzureFoundry__Endpoint=${AzureFoundry__Endpoint}
      - AzureFoundry__ProjectName=${AzureFoundry__ProjectName}
      - AzureFoundry__SubscriptionId=${AzureFoundry__SubscriptionId}
      - AzureFoundry__ResourceGroup=${AzureFoundry__ResourceGroup}
      - AzureFoundry__SelectorAgentId=${AzureFoundry__SelectorAgentId}
      - VectorStorage__Endpoint=${VectorStorage__Endpoint}
      - VectorStorage__IndexName=user-context
      - VectorStorage__EmbeddingDimensions=1536
      - Embedding__DeploymentName=text-embedding-ada-002
      - Embedding__Endpoint=${Embedding__Endpoint}
      - WorkIQ__Endpoint=${WorkIQ__Endpoint}
      - WorkIQ__ApiVersion=2024-12-01-preview
      - WorkIQ__Scope=https://workiq.microsoft.com/.default
      - WorkIQ__Enabled=true
      - WorkIQ__MaxItemsPerQuery=10
      - WorkIQ__MaxContentLengthChars=2000
      - WorkIQ__MinRelevanceScore=0.5
      - Workhttps://orchestrator-foundry.services.ai.azure.com/api/projects/subscriptions/0685a6ae-234f-45d4-8765-0469aa51ceb0/resourceGroups/orchestrator-engine-rg/providers/Microsoft.CognitiveServices/accounts/orchestrator-foundry/projects/orchestrator-foundry    - APPLICATIONINSIGHTS_CONNECTIONorchestrator-foundryONINSIGHTS_CONNECTION_STRING}
    rest$SUBSCRIPTION_IDped
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/healthz"]
      interval: 30s
      timeout: 3s
      retries: 3
      start_period: 40s
```

Run with:
```bash
export $(cat .env.production | xargs)
docker-compose -f docker-compose.production.yml up -d
```

---

## Deployment Options

### Option 1: Azure Container Instances (Simplest for Dev/Test)

```bash
ACE_NAME="orchestrator-engine-container"
CONTAINER_NAME="orchestrator-engine"

MSYS_NO_PATHCONV=1 az container create \
  --resource-group $RESOURCE_GROUP \
  --name $ACE_NAME \
  --image $IMAGE_URL \
  --os-type Linux \
  --ports 8080 8081 \
  --registry-login-server $REGISTRY_URL \
  --registry-username $REGISTRY_USERNAME \
  --registry-password $REGISTRY_PASSWORD \
  --assign-identity $IDENTITY_ID \
  --environment-variables \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_HTTPS_PORTS=8081 \
    AzureFoundry__Endpoint="https://orchestrator-foundry.services.ai.azure.com/api/projects/orchestrator-foundry" \
    AzureFoundry__ProjectName="orchestrator-foundry" \
    AzureFoundry__SubscriptionId="$SUBSCRIPTION_ID" \
    AzureFoundry__ResourceGroup="$RESOURCE_GROUP" \
    VectorStorage__Endpoint="$SEARCH_ENDPOINT" \
    VectorStorage__IndexName="user-context" \
    Embedding__Endpoint="$OPENAI_ENDPOINT" \
    Embedding__DeploymentName="text-embedding-ada-002" \
  --memory 3 \
  --cpu 1 \
  --dns-name-label orchestrator-engine

# Get public IP
az container show \
  --resource-group $RESOURCE_GROUP \
  --name $ACE_NAME \
  --query ipAddress.fqdn -o tsv
```

### Option 2: Azure App Service (Production)

```bash
APP_SERVICE_PLAN="orchestrator-asp"
APP_NAME="orchestrator-engine-app"

# Create App Service Plan
az appservice plan create \
  --name $APP_SERVICE_PLAN \
  --resource-group $RESOURCE_GROUP \
  --sku B2 \
  --is-linux

# Create App Service
az webapp create \
  --resource-group $RESOURCE_GROUP \
  --plan $APP_SERVICE_PLAN \
  --name $APP_NAME \
  --deployment-container-image-name $IMAGE_URL \
  --registry-url $REGISTRY_URL \
  --registry-username $REGISTRY_USERNAME \
  --registry-password $REGISTRY_PASSWORD

# Assign Managed Identity
az webapp identity assign \
  --resource-group $RESOURCE_GROUP \
  --name $APP_NAME \
  --identities $IDENTITY_ID

# Set app settings
az webapp config appsettings set \
  --resource-group $RESOURCE_GROUP \
  --name $APP_NAME \
  --settings \
    ASPNETCORE_ENVIRONMENT=Production \
    AzureFoundry__Endpoint="https://<your-foundry-project>.services.ai.azure.com/api/projects/<project-id>" \
    AzureFoundry__ProjectName="<your-project-name>" \
    AzureFoundry__SubscriptionId="$SUBSCRIPTION_ID" \
    AzureFoundry__ResourceGroup="$RESOURCE_GROUP" \
    VectorStorage__Endpoint="$SEARCH_ENDPOINT" \
    Embedding__Endpoint="$OPENAI_ENDPOINT" \
    APPLICATIONINSIGHTS_CONNECTION_STRING="InstrumentationKey=$APPINSIGHTS_KEY"

# Get App URL
az webapp show \
  --resource-group $RESOURCE_GROUP \
  --name $APP_NAME \
  --query defaultHostName -o tsv
```

### Option 3: Azure Kubernetes Service (Scalable)

```bash
# Create AKS cluster
CLUSTER_NAME="orchestrator-aks"

az aks create \
  --resource-group $RESOURCE_GROUP \
  --name $CLUSTER_NAME \
  --node-count 2 \
  --vm-set-type VirtualMachineScaleSets \
  --enable-managed-identity \
  --network-plugin azure \
  --enable-addons monitoring \
  --workspace-resource-id "/subscriptions/$SUBSCRIPTION_ID/resourcegroups/$RESOURCE_GROUP/providers/microsoft.operationalinsights/workspaces/$APPINSIGHTS_NAME"

# Get credentials
az aks get-credentials \
  --resource-group $RESOURCE_GROUP \
  --name $CLUSTER_NAME

# Create namespace
kubectl create namespace orchestrator

# Create image pull secret
kubectl create secret docker-registry acr-secret \
  --docker-server=$REGISTRY_URL \
  --docker-username=$REGISTRY_USERNAME \
  --docker-password=$REGISTRY_PASSWORD \
  -n orchestrator

# Create Kubernetes deployment (see deployment.yaml below)
kubectl apply -f k8s-deployment.yaml -n orchestrator
```

Create `k8s-deployment.yaml`:

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: orchestrator-engine
  namespace: orchestrator
  labels:
    app: orchestrator-engine
spec:
  replicas: 3
  selector:
    matchLabels:
      app: orchestrator-engine
  template:
    metadata:
      labels:
        app: orchestrator-engine
    spec:
      serviceAccountName: orchestrator-engine
      imagePullSecrets:
      - name: acr-secret
      containers:
      - name: orchestrator-engine
        image: <your-registry>.azurecr.io/orchestrator-engine:latest
        imagePullPolicy: Always
        ports:
        - containerPort: 8080
          name: http
        - containerPort: 8081
          name: https
        env:
        - name: ASPNETCORE_ENVIRONMENT
          value: "Production"
        - name: ASPNETCORE_HTTP_PORTS
          value: "8080"
        - name: ASPNETCORE_HTTPS_PORTS
          value: "8081"
        - name: AzureFoundry__Endpoint
          valueFrom:
            configMapKeyRef:
              name: orchestrator-config
              key: foundry-endpoint
        - name: AzureFoundry__ProjectName
          valueFrom:
            configMapKeyRef:
              name: orchestrator-config
              key: foundry-project-name
        - name: AzureFoundry__SubscriptionId
          valueFrom:
            configMapKeyRef:
              name: orchestrator-config
              key: subscription-id
        - name: AzureFoundry__ResourceGroup
          valueFrom:
            configMapKeyRef:
              name: orchestrator-config
              key: resource-group
        - name: VectorStorage__Endpoint
          valueFrom:
            configMapKeyRef:
              name: orchestrator-config
              key: search-endpoint
        - name: Embedding__Endpoint
          valueFrom:
            configMapKeyRef:
              name: orchestrator-config
              key: openai-endpoint
        livenessProbe:
          httpGet:
            path: /healthz
            port: 8080
          initialDelaySeconds: 40
          periodSeconds: 30
          timeoutSeconds: 3
          failureThreshold: 3
        readinessProbe:
          httpGet:
            path: /healthz
            port: 8080
          initialDelaySeconds: 20
          periodSeconds: 10
          timeoutSeconds: 3
          failureThreshold: 3
        resources:
          requests:
            memory: "512Mi"
            cpu: "250m"
          limits:
            memory: "1Gi"
            cpu: "500m"

---
apiVersion: v1
kind: Service
metadata:
  name: orchestrator-engine-service
  namespace: orchestrator
spec:
  type: LoadBalancer
  ports:
  - port: 80
    targetPort: 8080
    protocol: TCP
    name: http
  - port: 443
    targetPort: 8081
    protocol: TCP
    name: https
  selector:
    app: orchestrator-engine

---
apiVersion: v1
kind: ConfigMap
metadata:
  name: orchestrator-config
  namespace: orchestrator
data:
  foundry-endpoint: "https://<your-foundry-project>.services.ai.azure.com/api/projects/<project-id>"
  foundry-project-name: "<your-project-name>"
  subscription-id: "<your-subscription-id>"
  resource-group: "<your-resource-group>"
  search-endpoint: "https://<your-search-service>.search.windows.net"
  openai-endpoint: "https://<your-openai-endpoint>.openai.azure.com/"

---
apiVersion: v1
kind: ServiceAccount
metadata:
  name: orchestrator-engine
  namespace: orchestrator
```

---

## Database & Vector Index Setup

### Azure AI Search Index Creation

The index is auto-created on first run by `VectorStorageService.EnsureIndexExistsAsync()`, but here's the schema:

**Index Name**: `user-context`

**Fields**:
```
- Id (string, key, filterable)
- UserId (string, filterable)
- Content (string, searchable)
- Domain (string, filterable)
- CreatedAt (DateTimeOffset, filterable, sortable)
- ContentVector (vector, 1536 dimensions, HNSW algorithm)
```

**Vector Search Config**:
- Algorithm: HNSW
- Profile: default-profile
- Similarity Metric: cosine

**Manual Index Creation** (if needed):

```bash
# Login to Azure
az login

# Create index via Azure portal or use Azure Search REST API
curl -X PUT "$SEARCH_ENDPOINT/indexes/user-context?api-version=2024-09-01-preview" \
  -H "Content-Type: application/json" \
  -H "api-key: $SEARCH_API_KEY" \
  -d @- << 'EOF'
{
  "name": "user-context",
  "fields": [
    {
      "name": "Id",
      "type": "Edm.String",
      "key": true,
      "filterable": true
    },
    {
      "name": "UserId",
      "type": "Edm.String",
      "filterable": true
    },
    {
      "name": "Content",
      "type": "Edm.String",
      "searchable": true
    },
    {
      "name": "Domain",
      "type": "Edm.String",
      "filterable": true
    },
    {
      "name": "CreatedAt",
      "type": "Edm.DateTimeOffset",
      "filterable": true,
      "sortable": true
    },
    {
      "name": "ContentVector",
      "type": "Collection(Edm.Single)",
      "dimensions": 1536,
      "vectorSearchProfile": "default-profile",
      "searchable": true
    }
  ],
  "vectorSearch": {
    "algorithms": [
      {
        "name": "default-algorithm",
        "kind": "hnsw"
      }
    ],
    "profiles": [
      {
        "name": "default-profile",
        "algorithmConfigurationName": "default-algorithm"
      }
    ]
  }
}
EOF
```

### Vector Index Queries

The service uses:
- **Store**: `await _searchClient.MergeOrUploadDocumentsAsync()`
- **Search**: Vector semantic search with HNSW similarity
- **Filter**: By UserId and Domain
- **Top K**: Configurable (default 5)

---

## Endpoints & API Reference

### Base URL
```
http://localhost:8080/api/orchestrator
or
https://<your-app>.azurewebsites.net/api/orchestrator
```

### Endpoints

#### 1. Process Orchestration
**POST** `/process`

**Request**:
```json
{
  "userId": "user@example.com",
  "prompt": "How do I create a new Azure resource group?",
  "intent": "infrastructure",
  "context": {
    "domain": "azure-cli",
    "sessionId": "session-123"
  }
}
```

**Response**:
```json
{
  "orchestrationId": "orch-123",
  "selectedAgent": {
    "agentId": "agent-456",
    "name": "Azure Infrastructure Agent",
    "description": "Helps with Azure resource management",
    "provider": "azure-foundry",
    "capabilities": ["create-resources", "manage-infrastructure"]
  },
  "options": [
    {
      "id": "opt-1",
      "label": "Create Resource Group",
      "description": "Step-by-step guide for creating a resource group"
    }
  ],
  "confidence": 0.95,
  "timestamp": "2024-09-12T10:30:00Z"
}
```

#### 2. Agent Discovery
**POST** `/discover`

**Request**:
```json
{
  "userId": "user@example.com",
  "prompt": "Find agents for data analysis",
  "intent": ""
}
```

**Response**:
```json
{
  "orchestrationId": "orch-124",
  "discoveredAgents": [
    {
      "agentId": "agent-001",
      "name": "Data Analysis Agent",
      "description": "Analyzes data and provides insights",
      "provider": "azure-foundry",
      "capabilities": ["data-analysis", "visualization"]
    },
    {
      "agentId": "agent-002",
      "name": "SQL Query Agent",
      "description": "Generates and optimizes SQL queries",
      "provider": "azure-foundry",
      "capabilities": ["sql-generation", "query-optimization"]
    }
  ],
  "timestamp": "2024-09-12T10:30:00Z"
}
```

#### 3. Health Check
**GET** `/healthz`

**Response**:
```
200 OK
```

#### 4. OpenAPI Documentation (Dev only)
**GET** `/openapi/v1.json`

---

## Monitoring & Logging

### Application Insights Integration

Logs are automatically sent to Application Insights. View via:

```bash
# Get connection string
az monitor app-insights component show \
  --app $APPINSIGHTS_NAME \
  --query connectionString -o tsv
```

### Key Metrics to Monitor

1. **Availability**: Orchestration endpoint response time
2. **Performance**: Vector search latency, embedding generation time
3. **Errors**: Failed agent calls, search index errors
4. **Custom Events**: Agent selection, user context stored
5. **Dependencies**: Azure AI Foundry, Search, OpenAI latency

### Query Examples in Application Insights

**Failed Requests**:
```kusto
requests
| where success == false
| project timestamp, name, resultCode, duration, customDimensions
| order by timestamp desc
```

**Vector Search Duration**:
```kusto
customEvents
| where name == "VectorSearchCompleted"
| project timestamp, toreal(customMeasurements.DurationMs)
| summarize avg(treal_customMeasurements_DurationMs), max(treal_customMeasurements_DurationMs)
```

**Agent Selection Stats**:
```kusto
customEvents
| where name == "AgentSelected"
| summarize count() by tostring(customDimensions.agentId)
| order by count_ desc
```

### Azure Monitor Alerts

Create alerts for:
- Failed API calls > 5% error rate
- Response time > 5 seconds (p95)
- Vector storage quota usage > 80%
- OpenAI API errors

---

## Post-Deployment Validation

### 1. Verify Deployment

```bash
# Check container/app status
# For ACI
az container show --name $ACE_NAME --resource-group $RESOURCE_GROUP

# For App Service
az webapp show --name $APP_NAME --resource-group $RESOURCE_GROUP

# For AKS
kubectl get pods -n orchestrator
kubectl get svc -n orchestrator
```

### 2. Test Endpoints

```bash
# Get service endpoint
ENDPOINT="http://localhost:8080"  # or your deployed URL

# Health check
curl -v $ENDPOINT/healthz

# Process endpoint
curl -X POST "$ENDPOINT/api/orchestrator/process" \
  -H "Content-Type: application/json" \
  -d '{
    "userId": "test@example.com",
    "prompt": "Test prompt",
    "intent": "test"
  }'

# Discover endpoint
curl -X POST "$ENDPOINT/api/orchestrator/discover" \
  -H "Content-Type: application/json" \
  -d '{
    "userId": "test@example.com",
    "prompt": "Find agents"
  }'
```

### 3. Vector Index Validation

```bash
# List indices
curl -X GET "$SEARCH_ENDPOINT/indexes?api-version=2024-09-01-preview" \
  -H "api-key: $SEARCH_API_KEY"

# Check index statistics
curl -X GET "$SEARCH_ENDPOINT/indexes/user-context/stats?api-version=2024-09-01-preview" \
  -H "api-key: $SEARCH_API_KEY"
```

### 4. Authentication Validation

```bash
# Verify Managed Identity has correct roles
az role assignment list --assignee $IDENTITY_CLIENT_ID --output table

# Test Azure credential
az account show --use-cli-only
```

### 5. Check Logs

```bash
# Container logs (ACI)
az container logs --name $ACE_NAME --resource-group $RESOURCE_GROUP

# App Service logs
az webapp log tail --name $APP_NAME --resource-group $RESOURCE_GROUP

# AKS logs
kubectl logs -n orchestrator -l app=orchestrator-engine --tail=100
```

---

## Troubleshooting

### Common Issues

| Issue | Cause | Solution |
|-------|-------|----------|
| 401 Unauthorized on Azure Search | Missing/wrong RBAC role | Verify Search Index Data Contributor role assigned |
| 401 on OpenAI | Missing Cognitive Services User role | Add Cognitive Services OpenAI User role |
| Vector search returns empty | Index not created | Check `EnsureIndexExistsAsync()` logs, manually create index |
| WorkIQ endpoint not responding | Wrong endpoint/disabled | Verify endpoint in config, ensure WorkIQ__Enabled=true |
| Slow embedding generation | OpenAI quota exhausted | Check OpenAI deployment capacity, increase if needed |
| High latency on Foundry calls | Network/foundry issues | Check firewall rules, verify Foundry project status |
| MissingSubscription error on role assignment | Azure CLI has no subscription context | Run `az account set --subscription 0685a6ae-234f-45d4-8765-0469aa51ceb0` first |
| MissingSubscription error (alternate) | Variables empty or not exported | Run full Step 8b export block, verify with `echo $IDENTITY_CLIENT_ID` |

### Debugging Steps

1. **Check Application Insights** for error details
2. **Review container logs** for startup errors
3. **Verify configuration** with deployed app settings
4. **Test Azure CLI credentials** `az login`
5. **Validate vector index** exists and has documents
6. **Monitor Azure services** for quotas/throttling

---

## Security Best Practices

1. **Never commit secrets** to repository
2. **Use Managed Identity** for Azure service authentication
3. **Use Key Vault** for sensitive configuration
4. **Enable Network Security Groups** to restrict traffic
5. **Use Private Endpoints** for Azure services
6. **Enable Azure Defender** for threat detection
7. **Implement CORS** restrictions
8. **Use HTTPS** only in production
9. **Rotate credentials** regularly
10. **Audit access logs** in Application Insights

---

## Cost Optimization

1. **Azure AI Search**: Use "Free" tier for dev (1 index, 50MB limit)
2. **Azure OpenAI**: Reserve capacity for predictable workloads
3. **Container Instances**: Use for dev/test only
4. **App Service**: Use B-series for production
5. **Cosmos DB**: Not currently used; consider for future persistence
6. **Monitor costs** with Azure Cost Management

---

## Next Steps

1. ✅ Create Azure resources using scripts above
2. ✅ Configure appsettings and environment variables
3. ✅ Build and push Docker image to registry
4. ✅ Deploy using your chosen option (ACI/App Service/AKS)
5. ✅ Validate endpoints are responding
6. ✅ Monitor logs and metrics in Application Insights
7. ✅ Set up CI/CD pipeline (GitHub Actions/ADO)
8. ✅ Implement custom agents in Azure AI Foundry
9. ✅ Configure alerting and on-call procedures
10. ✅ Create runbooks for common operational tasks

---

## Support & Documentation

- **Azure AI Foundry**: https://learn.microsoft.com/en-us/azure/ai-services/agents/
- **Azure AI Search**: https://learn.microsoft.com/en-us/azure/search/
- **Azure OpenAI**: https://learn.microsoft.com/en-us/azure/ai-services/openai/
- **WorkIQ API**: https://learn.microsoft.com/en-us/graph/api/workiq-overview
- **ASP.NET Core**: https://learn.microsoft.com/en-us/aspnet/core/
- **.NET 10.0**: https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10
