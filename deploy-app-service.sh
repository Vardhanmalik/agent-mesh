#!/bin/bash

# ==============================================================================
# Orchestrator Engine - Deployment Script (App Service)
# ==============================================================================
# Deploys the Orchestrator Engine to Azure App Service
# ==============================================================================

set -e

# Load environment
if [ ! -f .env.production ]; then
    echo "Error: .env.production not found. Run azure-setup.sh first."
    exit 1
fi

source .env.production

BLUE='\033[0;34m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

# Configuration
APP_SERVICE_PLAN="${APP_SERVICE_PLAN:-orchestrator-asp}"
APP_NAME="${APP_NAME:-orchestrator-engine-app}"

echo -e "${BLUE}============================================${NC}"
echo -e "${BLUE}Deploying to Azure App Service${NC}"
echo -e "${BLUE}============================================${NC}"
echo ""

# Step 1: Create App Service Plan
echo -e "${BLUE}Creating App Service Plan...${NC}"
if az appservice plan show --name $APP_SERVICE_PLAN --resource-group $RESOURCE_GROUP &>/dev/null; then
    echo -e "${YELLOW}  ⚠ App Service Plan already exists${NC}"
else
    az appservice plan create \
        --name $APP_SERVICE_PLAN \
        --resource-group $RESOURCE_GROUP \
        --sku B2 \
        --is-linux
    echo -e "${GREEN}  ✓ App Service Plan created${NC}"
fi
echo ""

# Step 2: Create Web App
echo -e "${BLUE}Creating Web App...${NC}"
if az webapp show --name $APP_NAME --resource-group $RESOURCE_GROUP &>/dev/null; then
    echo -e "${YELLOW}  ⚠ Web App already exists${NC}"
else
    az webapp create \
        --resource-group $RESOURCE_GROUP \
        --plan $APP_SERVICE_PLAN \
        --name $APP_NAME \
        --deployment-container-image-name "$REGISTRY_URL/orchestrator-engine:latest"
    echo -e "${GREEN}  ✓ Web App created${NC}"
fi
echo ""

# Step 3: Configure Registry Credentials
echo -e "${BLUE}Configuring registry credentials...${NC}"
az webapp config container set \
    --resource-group $RESOURCE_GROUP \
    --name $APP_NAME \
    --docker-custom-image-name "$REGISTRY_URL/orchestrator-engine:latest" \
    --docker-registry-server-url "https://$REGISTRY_URL" \
    --docker-registry-server-user $REGISTRY_USERNAME \
    --docker-registry-server-password "$REGISTRY_PASSWORD"
echo -e "${GREEN}  ✓ Registry credentials configured${NC}"
echo ""

# Step 4: Assign Managed Identity
echo -e "${BLUE}Assigning Managed Identity...${NC}"
az webapp identity assign \
    --resource-group $RESOURCE_GROUP \
    --name $APP_NAME \
    --identities $IDENTITY_ID
echo -e "${GREEN}  ✓ Managed Identity assigned${NC}"
echo ""

# Step 5: Configure App Settings
echo -e "${BLUE}Configuring application settings...${NC}"
az webapp config appsettings set \
    --resource-group $RESOURCE_GROUP \
    --name $APP_NAME \
    --settings \
        ASPNETCORE_ENVIRONMENT=Production \
        ASPNETCORE_HTTP_PORTS=8080 \
        ASPNETCORE_HTTPS_PORTS=8081 \
        AzureFoundry__Endpoint="https://<your-foundry-project>.services.ai.azure.com/api/projects/<project-id>" \
        AzureFoundry__ProjectName="<your-project-name>" \
        AzureFoundry__SubscriptionId="$SUBSCRIPTION_ID" \
        AzureFoundry__ResourceGroup="$RESOURCE_GROUP" \
        AzureFoundry__SelectorAgentId="" \
        VectorStorage__Endpoint="$SEARCH_ENDPOINT" \
        VectorStorage__IndexName="user-context" \
        VectorStorage__EmbeddingDimensions="1536" \
        Embedding__DeploymentName="text-embedding-ada-002" \
        Embedding__Endpoint="$OPENAI_ENDPOINT" \
        APPLICATIONINSIGHTS_CONNECTION_STRING="$APPINSIGHTS_CONNECTION"
echo -e "${GREEN}  ✓ Application settings configured${NC}"
echo ""

# Step 6: Enable Logging
echo -e "${BLUE}Enabling application logging...${NC}"
az webapp log config \
    --name $APP_NAME \
    --resource-group $RESOURCE_GROUP \
    --application-logging true \
    --detailed-error-messages true \
    --failed-request-tracing true
echo -e "${GREEN}  ✓ Logging enabled${NC}"
echo ""

# Step 7: Start the app
echo -e "${BLUE}Starting the app...${NC}"
az webapp start \
    --name $APP_NAME \
    --resource-group $RESOURCE_GROUP
echo -e "${GREEN}  ✓ App started${NC}"
echo ""

# Get App URL
APP_URL=$(az webapp show \
    --name $APP_NAME \
    --resource-group $RESOURCE_GROUP \
    --query defaultHostName -o tsv)

# Summary
echo -e "${BLUE}============================================${NC}"
echo -e "${GREEN}✓ Deployment complete!${NC}"
echo -e "${BLUE}============================================${NC}"
echo ""
echo -e "${YELLOW}Application Details:${NC}"
echo "  Name: $APP_NAME"
echo "  URL: https://$APP_URL"
echo "  App Service Plan: $APP_SERVICE_PLAN"
echo "  Resource Group: $RESOURCE_GROUP"
echo ""
echo -e "${YELLOW}Next Steps:${NC}"
echo "1. Wait 2-3 minutes for the container to start"
echo "2. Test health endpoint: curl https://$APP_URL/healthz"
echo "3. Check logs: az webapp log tail --name $APP_NAME --resource-group $RESOURCE_GROUP"
echo "4. Update Azure Foundry endpoint in app settings"
echo "5. Monitor in Application Insights"
echo ""
echo "Tips:"
echo "  - View logs: az webapp log tail -n $APP_NAME -g $RESOURCE_GROUP --tail 100"
echo "  - Scale up: az appservice plan update -n $APP_SERVICE_PLAN -g $RESOURCE_GROUP --sku P1V2"
echo "  - Restart: az webapp restart -n $APP_NAME -g $RESOURCE_GROUP"
