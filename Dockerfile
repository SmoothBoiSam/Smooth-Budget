# One container for the whole app: the API serves the built website from the same origin (DECISIONS.md 123).

# 1. The website.
FROM node:22-bookworm-slim AS web
WORKDIR /web
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

# 2. The API.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY backend/src/SmoothBudget.Core/SmoothBudget.Core.csproj backend/src/SmoothBudget.Core/
COPY backend/src/SmoothBudget.Api/SmoothBudget.Api.csproj backend/src/SmoothBudget.Api/
RUN dotnet restore backend/src/SmoothBudget.Api/SmoothBudget.Api.csproj
COPY backend/src/ backend/src/
RUN dotnet publish backend/src/SmoothBudget.Api/SmoothBudget.Api.csproj -c Release -o /app --no-restore

# 3. What runs: the API with the website in wwwroot. Secrets come from the host's environment, never the image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /app ./
COPY --from=web /web/dist ./wwwroot
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Hosting__BehindProxy=true \
    Database__MigrateOnStartup=true
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SmoothBudget.Api.dll"]
