FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

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

# NOTE: ACI ignores Dockerfile HEALTHCHECK (it uses --liveness-probe / --readiness-probe
# on `az container create`). The aspnet base image does not ship curl, so a curl-based
# HEALTHCHECK would always fail in docker-compose / App Service. Use dotnet or wget
# instead if a Dockerfile HEALTHCHECK is desired.

# Force Kestrel to listen on HTTP only inside the container. TLS is terminated by
# the ingress (App Service / APIM / Front Door). This prevents the "no server
# certificate specified" startup crash that leaves ACI restarting with no logs.
ENV ASPNETCORE_URLS=http://+:8080

USER appuser

ENTRYPOINT ["dotnet", "OrchestratorEngine.Api.dll"]