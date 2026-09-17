#!/bin/bash

# ==============================================================================
# Orchestrator Engine - Azure Resource Creation Script
# ==============================================================================
# This script creates all necessary Azure resources for the Orchestrator Engine
# deployment. Run this with: bash ./azure-setup.sh
#
# Prerequisites:
# - Azure CLI 2.60+
# - Logged in with: az login
# - Appropriate permissions in Azure subscription
# ==============================================================================

set -e  # Exit on error

# Error logging
ERROR_LOG="azure-setup-errors.log"
> "$ERROR_LOG"  # Clear log file

# Function to log errors and display them
log_error() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] ERROR: $*" >> "$ERROR_LOG"
}

# Color output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
RESOURCE_GROUP="${RESOURCE_GROUP:-orchestrator-engine-rg}"
LOCATION="${LOCATION:-eastus}"
SEARCH_SERVICE_NAME="${SEARCH_SERVICE_NAME:-orchestrator-search}"
OPENAI_NAME="${OPENAI_NAME:-orchestrator-openai}"
REGISTRY_NAME="${REGISTRY_NAME:-orchestratoracr}"
IDENTITY_NAME="${IDENTITY_NAME:-orchestrator-engine-identity}"
APPINSIGHTS_NAME="${APPINSIGHTS_NAME:-orchestrator-appinsights}"

# Get subscription ID
echo "[DEBUG] Getting subscription ID..." >> "$ERROR_LOG"
SUBSCRIPTION_ID=$(az account show --query id -o tsv 2>&1)
SUBSCRIPTION_EXIT_CODE=$?
echo "[DEBUG] Subscription ID: $SUBSCRIPTION_ID (exit code: $SUBSCRIPTION_EXIT_CODE)" >> "$ERROR_LOG"

if [ $SUBSCRIPTION_EXIT_CODE -ne 0 ]; then
    echo "ERROR: Failed to get subscription ID. Are you logged in with 'az login'?" >&2
    echo "$SUBSCRIPTION_ID" >> "$ERROR_LOG"
    exit 1
fi

echo -e "${BLUE}============================================${NC}"
echo -e "${BLUE}Orchestrator Engine - Azure Setup${NC}"
echo -e "${BLUE}============================================${NC}"
echo ""
echo -e "${YELLOW}Configuration:${NC}"
echo "  Resource Group: $RESOURCE_GROUP"
echo "  Location: $LOCATION"
echo "  Subscription: $SUBSCRIPTION_ID"
echo ""

# Register required resource providers and wait for full propagation
echo -e "${BLUE}Registering resource providers...${NC}"

PROVIDERS=("Microsoft.Search" "Microsoft.CognitiveServices" "Microsoft.ContainerRegistry" "Microsoft.ManagedIdentity" "Microsoft.Insights")

for provider in "${PROVIDERS[@]}"; do
    echo "  Registering $provider..."
    PROVIDER_OUTPUT=$(az provider register --namespace "$provider" --accept-terms 2>&1)
    PROVIDER_EXIT_CODE=$?
    
    if [ $PROVIDER_EXIT_CODE -ne 0 ]; then
        echo "[WARNING] Provider registration returned exit code $PROVIDER_EXIT_CODE for $provider" >> "$ERROR_LOG"
        echo "  $PROVIDER_OUTPUT" >> "$ERROR_LOG"
    fi
done

echo -e "${YELLOW}  Waiting for providers to be fully registered (this may take up to 2 minutes)...${NC}"

# Wait for all providers to be in "Registered" state
MAX_WAIT=120  # 2 minutes
ELAPSED=0
INTERVAL=10

while [ $ELAPSED -lt $MAX_WAIT ]; do
    ALL_REGISTERED=true
    
    for provider in "${PROVIDERS[@]}"; do
        STATE=$(az provider show --namespace "$provider" --query "registrationState" -o tsv 2>/dev/null)
        if [ "$STATE" != "Registered" ]; then
            ALL_REGISTERED=false
            echo "    ⏳ $provider: $STATE (waiting...)"
        fi
    done
    
    if [ "$ALL_REGISTERED" = true ]; then
        echo -e "${GREEN}  ✓ All providers globally registered${NC}"
        break
    fi
    
    sleep $INTERVAL
    ELAPSED=$((ELAPSED + INTERVAL))
done

if [ "$ALL_REGISTERED" != true ]; then
    echo -e "${YELLOW}  ⚠ Provider registration still in progress, proceeding anyway...${NC}"
fi

echo ""
echo -e "${YELLOW}Subscription Context:${NC}"
CURRENT_SUBSCRIPTION=$(az account show --query id -o tsv)
echo "  Subscription: $CURRENT_SUBSCRIPTION"
echo "  Region: $LOCATION"
echo ""

# Additional wait to ensure propagation to subscription
echo -e "${YELLOW}Waiting additional 120 seconds for subscription-level propagation...${NC}"
sleep 120
echo -e "${GREEN}  ✓ Ready to proceed with resource creation${NC}"
echo ""
echo ""

# Step 1: Create Resource Group
echo -e "${BLUE}Step 1: Creating Resource Group...${NC}"
echo "[DEBUG] Checking if resource group '$RESOURCE_GROUP' exists..." >> "$ERROR_LOG"

RG_CHECK=$(az group exists --name $RESOURCE_GROUP 2>&1)
RG_CHECK_EXIT=$?
echo "[DEBUG] RG check exit code: $RG_CHECK_EXIT, output: $RG_CHECK" >> "$ERROR_LOG"

if echo "$RG_CHECK" | grep -q true; then
    echo -e "${YELLOW}  ⚠ Resource group already exists${NC}"
else
    echo "[DEBUG] Creating resource group..." >> "$ERROR_LOG"
    RG_CREATE=$(az group create \
        --name $RESOURCE_GROUP \
        --location $LOCATION 2>&1)
    RG_CREATE_EXIT=$?
    
    if [ $RG_CREATE_EXIT -eq 0 ]; then
        echo -e "${GREEN}  ✓ Resource group created${NC}"
        echo "[DEBUG] Resource group created successfully" >> "$ERROR_LOG"
    else
        echo -e "${RED}  ✗ Failed to create resource group${NC}"
        echo "$RG_CREATE" >> "$ERROR_LOG"
        echo "ERROR: Resource group creation failed with exit code $RG_CREATE_EXIT" >&2
        exit 1
    fi
fi
echo ""

# Step 2: Create Azure AI Search
echo -e "${BLUE}Step 2: Creating Azure AI Search...${NC}"
echo "[DEBUG] Checking if search service exists..." >> "$ERROR_LOG"

SEARCH_EXISTS=$(az search service exists --name $SEARCH_SERVICE_NAME --resource-group $RESOURCE_GROUP 2>&1)
SEARCH_EXISTS_EXIT=$?
echo "[DEBUG] Search exists check exit code: $SEARCH_EXISTS_EXIT" >> "$ERROR_LOG"

if echo "$SEARCH_EXISTS" | grep -q true 2>/dev/null; then
    echo -e "${YELLOW}  ⚠ Search service already exists${NC}"
    SEARCH_ENDPOINT=$(az search service show \
        --name $SEARCH_SERVICE_NAME \
        --resource-group $RESOURCE_GROUP \
        --query endpoint -o tsv)
else
    # Retry logic for creating search service with error capture
    RETRY_COUNT=0
    MAX_RETRIES=5
    RETRY_DELAY=30
    
    while [ $RETRY_COUNT -lt $MAX_RETRIES ]; do
        # Capture stderr to see actual error
        CREATION_ERROR=$(az search service create \
            --name $SEARCH_SERVICE_NAME \
            --resource-group $RESOURCE_GROUP \
            --sku free \
            --replica-count 1 \
            --partition-count 1 2>&1)
        
        if [ $? -eq 0 ]; then
            echo -e "${GREEN}  ✓ Azure AI Search created${NC}"
            break
        else
            RETRY_COUNT=$((RETRY_COUNT + 1))
            echo "$CREATION_ERROR" >> "$ERROR_LOG"
            
            if [ $RETRY_COUNT -lt $MAX_RETRIES ]; then
                echo -e "${YELLOW}  ⚠ Creation failed (attempt $RETRY_COUNT/$MAX_RETRIES)${NC}"
                echo -e "${YELLOW}     Error: $(echo "$CREATION_ERROR" | head -1)${NC}"
                echo -e "${YELLOW}     Retrying in ${RETRY_DELAY} seconds...${NC}"
                sleep $RETRY_DELAY
            else
                echo -e "${RED}  ✗ Failed to create Azure AI Search after $MAX_RETRIES attempts${NC}"
                echo -e "${RED}     Last error: $CREATION_ERROR${NC}"
                echo -e "${RED}     Full error log saved to: $ERROR_LOG${NC}"
                echo ""
                echo -e "${YELLOW}Diagnostic Information:${NC}"
                CURRENT_SUBSCRIPTION=$(az account show --query id -o tsv 2>/dev/null || echo "unknown")
                echo "  Subscription: $CURRENT_SUBSCRIPTION"
                echo "  Region: $LOCATION"
                echo "  Checking provider state..."
                az provider show --namespace Microsoft.Search --query "registrationState" -o tsv 2>/dev/null || echo "  Could not check provider state"
                echo ""
                exit 1
            fi
        fi
    done
    
    SEARCH_ENDPOINT=$(az search service show \
        --name $SEARCH_SERVICE_NAME \
        --resource-group $RESOURCE_GROUP \
        --query endpoint -o tsv)
fi
echo "  Endpoint: $SEARCH_ENDPOINT"

# Get Search API Key
SEARCH_API_KEY=$(az search admin-key show \
    --name $SEARCH_SERVICE_NAME \
    --resource-group $RESOURCE_GROUP \
    --query primaryKey -o tsv)
echo ""

# Step 3: Create Azure OpenAI
echo -e "${BLUE}Step 3: Creating Azure OpenAI...${NC}"
if az cognitiveservices account show --name $OPENAI_NAME --resource-group $RESOURCE_GROUP &>/dev/null; then
    echo -e "${YELLOW}  ⚠ OpenAI account already exists${NC}"
    OPENAI_ENDPOINT=$(az cognitiveservices account show \
        --name $OPENAI_NAME \
        --resource-group $RESOURCE_GROUP \
        --query properties.endpoint -o tsv)
else
    # Retry logic for creating OpenAI account with error capture
    RETRY_COUNT=0
    MAX_RETRIES=5
    RETRY_DELAY=30
    
    while [ $RETRY_COUNT -lt $MAX_RETRIES ]; do
        # Capture stderr to see actual error
        CREATION_ERROR=$(az cognitiveservices account create \
            --name $OPENAI_NAME \
            --resource-group $RESOURCE_GROUP \
            --kind OpenAI \
            --sku S0 \
            --location $LOCATION 2>&1)
        
        if [ $? -eq 0 ]; then
            echo -e "${GREEN}  ✓ Azure OpenAI created${NC}"
            break
        else
            RETRY_COUNT=$((RETRY_COUNT + 1))
            echo "$CREATION_ERROR" >> "$ERROR_LOG"
            
            if [ $RETRY_COUNT -lt $MAX_RETRIES ]; then
                echo -e "${YELLOW}  ⚠ Creation failed (attempt $RETRY_COUNT/$MAX_RETRIES)${NC}"
                echo -e "${YELLOW}     Error: $(echo "$CREATION_ERROR" | head -1)${NC}"
                echo -e "${YELLOW}     Retrying in ${RETRY_DELAY} seconds...${NC}"
                sleep $RETRY_DELAY
            else
                echo -e "${RED}  ✗ Failed to create Azure OpenAI after $MAX_RETRIES attempts${NC}"
                echo -e "${RED}     Last error: $CREATION_ERROR${NC}"
                echo -e "${RED}     Full error log saved to: $ERROR_LOG${NC}"
                exit 1
            fi
        fi
    done
    
    OPENAI_ENDPOINT=$(az cognitiveservices account show \
        --name $OPENAI_NAME \
        --resource-group $RESOURCE_GROUP \
        --query properties.endpoint -o tsv)
fi
echo "  Endpoint: $OPENAI_ENDPOINT"
echo ""

# Step 4: Deploy Embedding Model
echo -e "${BLUE}Step 4: Deploying text-embedding-ada-002...${NC}"
if az cognitiveservices account deployment show \
    --name $OPENAI_NAME \
    --resource-group $RESOURCE_GROUP \
    --deployment-id "text-embedding-ada-002" &>/dev/null; then
    echo -e "${YELLOW}  ⚠ Deployment already exists${NC}"
else
    # Retry logic for deployment
    RETRY_COUNT=0
    MAX_RETRIES=3
    while [ $RETRY_COUNT -lt $MAX_RETRIES ]; do
        if az cognitiveservices account deployment create \
            --name $OPENAI_NAME \
            --resource-group $RESOURCE_GROUP \
            --deployment-id "text-embedding-ada-002" \
            --model-name "text-embedding-ada-002" \
            --model-version "2" \
            --model-format "OpenAI" \
            --sku-name "Standard" \
            --sku-capacity 1 2>/dev/null; then
            echo -e "${GREEN}  ✓ Embedding model deployed${NC}"
            break
        else
            RETRY_COUNT=$((RETRY_COUNT + 1))
            if [ $RETRY_COUNT -lt $MAX_RETRIES ]; then
                echo -e "${YELLOW}  ⚠ Deployment failed, retrying in 10 seconds (attempt $((RETRY_COUNT + 1))/$MAX_RETRIES)...${NC}"
                sleep 10
            else
                echo -e "${RED}  ✗ Failed to deploy embedding model after $MAX_RETRIES attempts${NC}"
                exit 1
            fi
        fi
    done
fi
echo ""

# Step 5: Create Container Registry
echo -e "${BLUE}Step 5: Creating Container Registry...${NC}"
if az acr show --name $REGISTRY_NAME --resource-group $RESOURCE_GROUP &>/dev/null; then
    echo -e "${YELLOW}  ⚠ Registry already exists${NC}"
else
    # Retry logic for registry creation
    RETRY_COUNT=0
    MAX_RETRIES=3
    while [ $RETRY_COUNT -lt $MAX_RETRIES ]; do
        if az acr create \
            --resource-group $RESOURCE_GROUP \
            --name $REGISTRY_NAME \
            --sku Basic \
            --admin-enabled true 2>/dev/null; then
            echo -e "${GREEN}  ✓ Container Registry created${NC}"
            break
        else
            RETRY_COUNT=$((RETRY_COUNT + 1))
            if [ $RETRY_COUNT -lt $MAX_RETRIES ]; then
                echo -e "${YELLOW}  ⚠ Creation failed, retrying in 10 seconds (attempt $((RETRY_COUNT + 1))/$MAX_RETRIES)...${NC}"
                sleep 10
            else
                echo -e "${RED}  ✗ Failed to create Container Registry after $MAX_RETRIES attempts${NC}"
                exit 1
            fi
        fi
    done
fi

REGISTRY_URL=$(az acr show \
    --name $REGISTRY_NAME \
    --query loginServer -o tsv)
REGISTRY_USERNAME=$(az acr credential show \
    --name $REGISTRY_NAME \
    --query username -o tsv)
REGISTRY_PASSWORD=$(az acr credential show \
    --name $REGISTRY_NAME \
    --query passwords[0].value -o tsv)

echo "  Registry URL: $REGISTRY_URL"
echo "  Username: $REGISTRY_USERNAME"
echo ""

# Step 6: Create Managed Identity
echo -e "${BLUE}Step 6: Creating Managed Identity...${NC}"
if az identity show --resource-group $RESOURCE_GROUP --name $IDENTITY_NAME &>/dev/null; then
    echo -e "${YELLOW}  ⚠ Identity already exists${NC}"
else
    az identity create \
        --resource-group $RESOURCE_GROUP \
        --name $IDENTITY_NAME
    echo -e "${GREEN}  ✓ Managed Identity created${NC}"
fi

IDENTITY_ID=$(az identity show \
    --resource-group $RESOURCE_GROUP \
    --name $IDENTITY_NAME \
    --query id -o tsv)
IDENTITY_CLIENT_ID=$(az identity show \
    --resource-group $RESOURCE_GROUP \
    --name $IDENTITY_NAME \
    --query clientId -o tsv)

echo "  Identity ID: $IDENTITY_ID"
echo "  Client ID: $IDENTITY_CLIENT_ID"
echo ""

# Step 7: Grant RBAC Permissions
echo -e "${BLUE}Step 7: Granting RBAC Permissions...${NC}"

SEARCH_RESOURCE_ID="/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Search/searchServices/$SEARCH_SERVICE_NAME"
OPENAI_RESOURCE_ID="/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.CognitiveServices/accounts/$OPENAI_NAME"

# Search Index Data Reader
echo "  Assigning Search Index Data Reader..."
az role assignment create \
    --assignee $IDENTITY_CLIENT_ID \
    --role "Search Index Data Reader" \
    --scope $SEARCH_RESOURCE_ID \
    --skip-authorization-check 2>/dev/null || true

# Search Index Data Contributor
echo "  Assigning Search Index Data Contributor..."
az role assignment create \
    --assignee $IDENTITY_CLIENT_ID \
    --role "Search Index Data Contributor" \
    --scope $SEARCH_RESOURCE_ID \
    --skip-authorization-check 2>/dev/null || true

# Cognitive Services User
echo "  Assigning Cognitive Services User..."
az role assignment create \
    --assignee $IDENTITY_CLIENT_ID \
    --role "Cognitive Services User" \
    --scope $OPENAI_RESOURCE_ID \
    --skip-authorization-check 2>/dev/null || true

# Cognitive Services OpenAI User
echo "  Assigning Cognitive Services OpenAI User..."
az role assignment create \
    --assignee $IDENTITY_CLIENT_ID \
    --role "Cognitive Services OpenAI User" \
    --scope $OPENAI_RESOURCE_ID \
    --skip-authorization-check 2>/dev/null || true

echo -e "${GREEN}  ✓ RBAC permissions granted${NC}"
echo ""

# Step 8: Create Application Insights
echo -e "${BLUE}Step 8: Creating Application Insights...${NC}"
if az monitor app-insights component show --app $APPINSIGHTS_NAME --resource-group $RESOURCE_GROUP &>/dev/null; then
    echo -e "${YELLOW}  ⚠ Application Insights already exists${NC}"
else
    az monitor app-insights component create \
        --app $APPINSIGHTS_NAME \
        --location $LOCATION \
        --resource-group $RESOURCE_GROUP \
        --application-type web
    echo -e "${GREEN}  ✓ Application Insights created${NC}"
fi

APPINSIGHTS_KEY=$(az monitor app-insights component show \
    --app $APPINSIGHTS_NAME \
    --resource-group $RESOURCE_GROUP \
    --query instrumentationKey -o tsv)
APPINSIGHTS_CONNECTION=$(az monitor app-insights component show \
    --app $APPINSIGHTS_NAME \
    --resource-group $RESOURCE_GROUP \
    --query connectionString -o tsv)

echo "  Key: $APPINSIGHTS_KEY"
echo ""

# Generate .env file
echo -e "${BLUE}Step 9: Generating .env file...${NC}"
cat > .env.production << EOF
# Azure Subscription
SUBSCRIPTION_ID=$SUBSCRIPTION_ID

# Resource Group
RESOURCE_GROUP=$RESOURCE_GROUP

# Azure Search
SEARCH_SERVICE_NAME=$SEARCH_SERVICE_NAME
SEARCH_ENDPOINT=$SEARCH_ENDPOINT
SEARCH_API_KEY=$SEARCH_API_KEY

# Azure OpenAI
OPENAI_NAME=$OPENAI_NAME
OPENAI_ENDPOINT=$OPENAI_ENDPOINT

# Container Registry
REGISTRY_NAME=$REGISTRY_NAME
REGISTRY_URL=$REGISTRY_URL
REGISTRY_USERNAME=$REGISTRY_USERNAME
REGISTRY_PASSWORD=$REGISTRY_PASSWORD

# Managed Identity
IDENTITY_NAME=$IDENTITY_NAME
IDENTITY_ID=$IDENTITY_ID
IDENTITY_CLIENT_ID=$IDENTITY_CLIENT_ID

# Application Insights
APPINSIGHTS_NAME=$APPINSIGHTS_NAME
APPINSIGHTS_KEY=$APPINSIGHTS_KEY
APPINSIGHTS_CONNECTION="$APPINSIGHTS_CONNECTION"

# Application Settings
AzureFoundry__Endpoint=https://<your-foundry-project>.services.ai.azure.com/api/projects/<project-id>
AzureFoundry__ProjectName=<your-project-name>
AzureFoundry__SubscriptionId=$SUBSCRIPTION_ID
AzureFoundry__ResourceGroup=$RESOURCE_GROUP
AzureFoundry__SelectorAgentId=
VectorStorage__Endpoint=$SEARCH_ENDPOINT
VectorStorage__IndexName=user-context
VectorStorage__EmbeddingDimensions=1536
Embedding__DeploymentName=text-embedding-ada-002
Embedding__Endpoint=$OPENAI_ENDPOINT
EOF

echo -e "${GREEN}  ✓ .env.production created${NC}"
echo ""

# Summary
echo -e "${BLUE}============================================${NC}"
echo -e "${GREEN}✓ All resources created successfully!${NC}"
echo -e "${BLUE}============================================${NC}"
echo ""
echo -e "${YELLOW}Next Steps:${NC}"
echo "1. Update .env.production with your Azure AI Foundry details"
echo "2. Build Docker image: docker build -t $REGISTRY_URL/orchestrator-engine:latest ."
echo "3. Push to registry: docker push $REGISTRY_URL/orchestrator-engine:latest"
echo "4. Deploy using: ./deploy.sh"
echo ""
echo -e "${YELLOW}Environment file: .env.production${NC}"
echo ""

# Diagnostic message if there were any errors logged
if [ -s "$ERROR_LOG" ]; then
    echo -e "${YELLOW}⚠️  Diagnostics from execution:${NC}"
    echo "========================================="
    cat "$ERROR_LOG"
    echo "========================================="
    echo ""
    echo -e "${YELLOW}Full error log saved to: $ERROR_LOG${NC}"
    echo ""
fi
