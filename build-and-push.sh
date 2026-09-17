#!/bin/bash

# ==============================================================================
# Orchestrator Engine - Docker Build & Push Script
# ==============================================================================
# Builds and pushes the Docker image to Azure Container Registry
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
RED='\033[0;31m'
NC='\033[0m'

echo -e "${BLUE}============================================${NC}"
echo -e "${BLUE}Building and Pushing Docker Image${NC}"
echo -e "${BLUE}============================================${NC}"
echo ""

# Step 1: Login to ACR
echo -e "${BLUE}Logging in to Azure Container Registry...${NC}"
az acr login --name $REGISTRY_NAME
echo -e "${GREEN}✓ Logged in${NC}"
echo ""

# Step 2: Build image
echo -e "${BLUE}Building Docker image...${NC}"
docker build \
    -t $REGISTRY_URL/orchestrator-engine:latest \
    -t $REGISTRY_URL/orchestrator-engine:1.0.0 \
    -f Dockerfile \
    .
echo -e "${GREEN}✓ Image built successfully${NC}"
echo ""

# Step 3: Push image
echo -e "${BLUE}Pushing image to registry...${NC}"
docker push $REGISTRY_URL/orchestrator-engine:latest
docker push $REGISTRY_URL/orchestrator-engine:1.0.0
echo -e "${GREEN}✓ Image pushed successfully${NC}"
echo ""

# Step 4: Verify
echo -e "${BLUE}Verifying image in registry...${NC}"
az acr repository show-tags \
    --name $REGISTRY_NAME \
    --repository orchestrator-engine
echo -e "${GREEN}✓ Verification complete${NC}"
echo ""

echo -e "${BLUE}============================================${NC}"
echo -e "${GREEN}✓ Docker build and push complete!${NC}"
echo -e "${BLUE}============================================${NC}"
echo ""
echo "Image: $REGISTRY_URL/orchestrator-engine:latest"
