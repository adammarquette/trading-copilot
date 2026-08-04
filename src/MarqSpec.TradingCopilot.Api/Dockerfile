# Multi-stage build for the BFF. The SAME image runs locally (docker-compose) and on Railway (ADR-0012).
# Build context is the repo root: `docker build -f src/MarqSpec.TradingCopilot.Api/Dockerfile .`

# The SPA (gh#646, ADR-0020). The bundle ships INSIDE this image rather than on a separate origin, so there is one
# artifact to promote and no CORS boundary between client and API. Its own stage, first, because it shares nothing
# with the .NET build and its layers should cache on the lockfile alone.
FROM node:22-alpine AS client
WORKDIR /client
# Manifests first: this layer -- the expensive one -- re-runs only when dependencies actually change, not on every
# source edit.
COPY src/MarqSpec.TradingCopilot.Client/package.json src/MarqSpec.TradingCopilot.Client/package-lock.json ./
RUN npm ci
COPY src/MarqSpec.TradingCopilot.Client/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY .editorconfig ./
COPY src/ ./src/
# The ProjectX client submodule -- referenced by the venue adapter. Requires a recursive clone
# (`git submodule update --init`) before building the image.
COPY external/ ./external/
RUN dotnet publish src/MarqSpec.TradingCopilot.Api/MarqSpec.TradingCopilot.Api.csproj \
    -c Release -o /app /p:UseAppHost=false

# The built bundle becomes the API's wwwroot, which is what UseStaticFiles + the SPA fallback serve (ADR-0020).
# Copied AFTER the publish: publish writes the output directory, so a bundle placed first would be lost.
COPY --from=client /client/dist /app/wwwroot

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
