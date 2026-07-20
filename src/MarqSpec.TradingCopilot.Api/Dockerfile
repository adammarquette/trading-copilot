# Multi-stage build for the BFF. The SAME image runs locally (docker-compose) and on Railway (ADR-0012).
# Build context is the repo root: `docker build -f src/MarqSpec.TradingCopilot.Api/Dockerfile .`

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY .editorconfig ./
COPY src/ ./src/
RUN dotnet publish src/MarqSpec.TradingCopilot.Api/MarqSpec.TradingCopilot.Api.csproj \
    -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# Railway sets $PORT; default to 8080 for local compose.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MarqSpec.TradingCopilot.Api.dll"]
