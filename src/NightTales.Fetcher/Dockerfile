# src/NightTales.Fetcher/Dockerfile — the Fetcher image (02.5.2-T3, 02 D11/D19, [DECISION T40]).
# Multi-stage and non-root; both base images are pinned by digest (the tag is documentation), built from
# the repo root context like the Worker and Api images.
#
# The Fetcher is a console host with no listener (liveness = it can lease), so unlike the Api and the
# Worker (aspnet:10.0, which carries a shell and apt tooling) it needs only the base runtime. The final
# stage is therefore mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra: the distroless variant of
# the same .NET 10 runtime, with no shell, no package manager and no curl/wget, and a built-in non-root user.
# The -extra variant (not plain -chiseled) is required: it carries libicu and does not force invariant
# globalization. Plain -chiseled has no ICU and sets DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true, which would
# silently override Directory.Build.props (InvariantGlobalization=false) and change the IdnMapping (SourceUrl,
# PublicSuffixListProvider) and NFC (TextNormalizer) behaviour the Fetcher relies on.
#
# The image carries no secret: FETCHER_API_TOKEN and Fetcher__Internal__BaseUrl are supplied at run time.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
WORKDIR /src

# Committed lock files travel with each .csproj so `--locked-mode` checks the repository-pinned
# dependency closure (see the Api Dockerfile). Only the projects the Fetcher references are copied:
# Domain, Fetcher.Core and Fetcher (the Fetcher never references Application or Infrastructure).
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/NightTales.Domain/NightTales.Domain.csproj src/NightTales.Domain/packages.lock.json src/NightTales.Domain/
COPY src/NightTales.Fetcher.Core/NightTales.Fetcher.Core.csproj src/NightTales.Fetcher.Core/packages.lock.json src/NightTales.Fetcher.Core/
COPY src/NightTales.Fetcher/NightTales.Fetcher.csproj src/NightTales.Fetcher/packages.lock.json src/NightTales.Fetcher/
RUN dotnet restore src/NightTales.Fetcher/NightTales.Fetcher.csproj --locked-mode

COPY src/NightTales.Domain/ src/NightTales.Domain/
COPY src/NightTales.Fetcher.Core/ src/NightTales.Fetcher.Core/
COPY src/NightTales.Fetcher/ src/NightTales.Fetcher/
RUN dotnet publish src/NightTales.Fetcher/NightTales.Fetcher.csproj -c Release -o /app --no-restore \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra@sha256:b18ef5184a6afa186bdeb51cfff41751ab9f7daec068f1419af0982a0964ba90 AS runtime
WORKDIR /app
ENV DOTNET_EnableDiagnostics=0
COPY --from=build /app ./
# The image's own non-root user (APP_UID, 1654). No EXPOSE: the Fetcher opens no listening port.
USER $APP_UID
ENTRYPOINT ["dotnet", "NightTales.Fetcher.dll"]
