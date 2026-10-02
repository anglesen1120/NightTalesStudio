# src/NightTales.Worker/Dockerfile — the Worker image (11.1.1-T1, README §10 T24).
# Multi-stage and non-root; both base images are pinned by digest (the tag is documentation).
# The Worker builds a WebApplication for its /health/* Kestrel listener (09 W3 CR-42), so its base
# image is aspnet:10.0 — mcr.microsoft.com/dotnet/runtime:10.0 could not start it.
#
# 09.9.3-T1 replaces the health and logging surface; the entry point stays `dotnet NightTales.Worker.dll`.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
WORKDIR /src

# Committed lock files travel with each .csproj so `--locked-mode` checks the repository-pinned
# dependency closure (see the Api Dockerfile).
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/NightTales.Domain/NightTales.Domain.csproj src/NightTales.Domain/packages.lock.json src/NightTales.Domain/
COPY src/NightTales.RenderNode.Contract/NightTales.RenderNode.Contract.csproj src/NightTales.RenderNode.Contract/packages.lock.json src/NightTales.RenderNode.Contract/
COPY src/NightTales.Application/NightTales.Application.csproj src/NightTales.Application/packages.lock.json src/NightTales.Application/
COPY src/NightTales.Infrastructure/NightTales.Infrastructure.csproj src/NightTales.Infrastructure/packages.lock.json src/NightTales.Infrastructure/
COPY src/NightTales.Worker/NightTales.Worker.csproj src/NightTales.Worker/packages.lock.json src/NightTales.Worker/
RUN dotnet restore src/NightTales.Worker/NightTales.Worker.csproj --locked-mode

COPY src/ src/
COPY seed/ seed/
RUN dotnet publish src/NightTales.Worker/NightTales.Worker.csproj -c Release -o /app --no-restore \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f AS runtime
WORKDIR /app
ENV Worker__HealthPort=8082 \
    DOTNET_EnableDiagnostics=0
COPY --from=build /app ./
USER $APP_UID
# The health listener binds `Worker:HealthAddress` (default 127.0.0.1:8082, 09.2.6-T1). The Compose
# service overrides it to 0.0.0.0 so the published host port (127.0.0.1:8082 -> 8082, docker-compose.yml)
# can reach it; the app default stays loopback, and the published host side stays 127.0.0.1.
EXPOSE 8082
ENTRYPOINT ["dotnet", "NightTales.Worker.dll"]
