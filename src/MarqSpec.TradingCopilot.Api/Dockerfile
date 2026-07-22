# Multi-stage build for the BFF. The SAME image runs locally (docker-compose) and on Railway (ADR-0012).
# Build context is the repo root: `docker build -f src/MarqSpec.TradingCopilot.Api/Dockerfile .`

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY .editorconfig ./
COPY src/ ./src/
# The ProjectX client submodule -- referenced by the venue adapter. Requires a recursive clone
# (`git submodule update --init`) before building the image.
COPY external/ ./external/
RUN dotnet publish src/MarqSpec.TradingCopilot.Api/MarqSpec.TradingCopilot.Api.csproj \
    -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Npgsql probes for Kerberos/GSSAPI during the Postgres auth handshake; the base aspnet image does not
# ship libgssapi_krb5, so without it every startup logs a spurious "cannot open shared object file"
# error before falling back to password auth. Install it so startup logs stay clean, local and on
# Railway (gh#82). Placed before the COPY so this layer caches independently of app code.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./
# Railway sets $PORT; default to 8080 for local compose.
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MarqSpec.TradingCopilot.Api.dll"]
