# src/NightTales.Api/Dockerfile — the Api image (11.1.1-T1, README §10 T24).
# Multi-stage and non-root; both base images are pinned by digest (the tag is documentation).
# Used by the optional Compose stack (image smoke / e2e) and by 11.5.1-T1's deploy.yml.
#
# The build-time OpenAPI generator (Microsoft.Extensions.ApiDescription.Server) boots the Api and
# runs its start-up validation, which needs a connection string, an ES256 signing key, a public
# origin and the Data Protection KEK (docs/dev-setup.md). The image build does not produce that
# document — CI's build job does — so the generator is switched off here instead of baking
# throwaway secrets into a build stage.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
WORKDIR /src

# Central package management + committed lock files: restore before the sources so a source-only
# change reuses the layer. Each project's committed packages.lock.json travels with its .csproj, so
# `--locked-mode` checks the repository-pinned closure instead of accepting whatever it resolves.
# `.editorconfig` carries the analyzer severities the build treats as errors (CA1861 in the migration
# file, for one), so it belongs to the build inputs.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/NightTales.Domain/NightTales.Domain.csproj src/NightTales.Domain/packages.lock.json src/NightTales.Domain/
COPY src/NightTales.RenderNode.Contract/NightTales.RenderNode.Contract.csproj src/NightTales.RenderNode.Contract/packages.lock.json src/NightTales.RenderNode.Contract/
COPY src/NightTales.Application/NightTales.Application.csproj src/NightTales.Application/packages.lock.json src/NightTales.Application/
COPY src/NightTales.Infrastructure/NightTales.Infrastructure.csproj src/NightTales.Infrastructure/packages.lock.json src/NightTales.Infrastructure/
COPY src/NightTales.Api/NightTales.Api.csproj src/NightTales.Api/packages.lock.json src/NightTales.Api/
RUN dotnet restore src/NightTales.Api/NightTales.Api.csproj --locked-mode

# seed/ is a Content item of NightTales.Infrastructure (the Development auto-seed reads it from the
# output directory), so the publish needs it on disk.
COPY src/ src/
COPY seed/ seed/
# config/ holds the source-finder allow-list that NightTales.Infrastructure embeds by a literal path.
COPY config/ config/
RUN dotnet publish src/NightTales.Api/NightTales.Api.csproj -c Release -o /app --no-restore \
    -p:OpenApiGenerateDocuments=false -p:UseAppHost=false

# mcr.microsoft.com/dotnet/aspnet:10.0, not runtime: the Api is an ASP.NET Core host.
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
COPY --from=build /app ./
# The image's own non-root user (APP_UID, 1654 since .NET 8).
USER $APP_UID
EXPOSE 8080 8081
ENTRYPOINT ["dotnet", "NightTales.Api.dll"]
