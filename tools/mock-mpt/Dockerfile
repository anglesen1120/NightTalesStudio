# tools/mock-mpt/Dockerfile — the mock render node image (11.1.2-T1, 11 README D14/F25, 11 CR-16).
#
# The optional Compose service used by e2e.yml and image smoke; the daily path is native
# (`tools/dev/up.sh` runs `dotnet run --project tools/mock-mpt` on 127.0.0.1:8090). The same image can run
# as a staging render node: it then sets ASPNETCORE_ENVIRONMENT=Staging and serves HTTPS on 8090 with its own
# self-signed certificate pinned in the staging RenderNodes:Nodes config like a real node (README §10 T44).
#
# Like the Api and Worker images (README §10 T24) the build stage restores with --locked-mode against the
# committed packages.lock.json, and the runtime base is the digest-pinned ASP.NET Core image: the mock is a
# container service, so it runs framework-dependent. The `-r linux-x64 --self-contained` rule of D3/E8 applies
# to the *node bundle*, which carries no .NET runtime; a container base does.
#
# Nothing binary is committed: the media stage generates the canned 10-second MP4s with `ffmpeg -f lavfi`
# (F25), and the frozen contract fixtures are copied from the working tree (README §10 T33).
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
WORKDIR /src

# Central package management + committed lock files: restore before the sources so a source-only change reuses
# the layer (see the Api Dockerfile). `.editorconfig` carries the analyzer severities the build treats as
# errors, so it is a build input too.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/NightTales.RenderNode.Contract/NightTales.RenderNode.Contract.csproj src/NightTales.RenderNode.Contract/packages.lock.json src/NightTales.RenderNode.Contract/
COPY src/NightTales.RenderNode.Server/NightTales.RenderNode.Server.csproj src/NightTales.RenderNode.Server/packages.lock.json src/NightTales.RenderNode.Server/
COPY tools/mock-mpt/NightTales.MockMpt.csproj tools/mock-mpt/packages.lock.json tools/mock-mpt/
RUN dotnet restore tools/mock-mpt/NightTales.MockMpt.csproj --locked-mode

COPY src/NightTales.RenderNode.Contract/ src/NightTales.RenderNode.Contract/
COPY src/NightTales.RenderNode.Server/ src/NightTales.RenderNode.Server/
COPY tools/mock-mpt/ tools/mock-mpt/
RUN dotnet publish tools/mock-mpt/NightTales.MockMpt.csproj -c Release -o /app --no-restore \
    -p:UseAppHost=false

# The canned media stage (F25): `testsrc2` + a 440 Hz sine, 10 s, h264/aac, faststart, one file per aspect.
# The build fails when either file is missing or empty, so a runtime 500 for missing media is unreachable.
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS media
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /media \
    && ffmpeg -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=1920x1080:rate=30 \
        -f lavfi -i sine=frequency=440 -t 10 -c:v libx264 -c:a aac -movflags +faststart /media/landscape.mp4 \
    && ffmpeg -hide_banner -loglevel error -y -f lavfi -i testsrc2=size=1080x1920:rate=30 \
        -f lavfi -i sine=frequency=440 -t 10 -c:v libx264 -c:a aac -movflags +faststart /media/portrait.mp4 \
    && test -s /media/landscape.mp4 \
    && test -s /media/portrait.mp4

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f AS runtime
WORKDIR /app

# The container binds every interface because it cannot bind the host's 127.0.0.1; the service is
# `expose`-only (compose-network only, README D14), and the native run keeps the loopback default. The
# fixtures come from the working tree here; the Compose service overlays /fixtures with a read-only bind mount
# so fixture edits need no rebuild (README §10 T33).
ENV ASPNETCORE_URLS=http://+:8090 \
    ASPNETCORE_ENVIRONMENT=Development \
    MOCK_FIXTURES_DIR=/fixtures \
    MOCK_MEDIA_DIR=/media \
    DOTNET_EnableDiagnostics=0

COPY --from=build /app ./
COPY --from=media /media /media
COPY worker-node/contract/fixtures /fixtures

# The mock writes each task's SRT next to the canned media, so the runtime user must own /media.
RUN chown -R $APP_UID /media
USER $APP_UID

EXPOSE 8090
ENTRYPOINT ["dotnet", "NightTales.MockMpt.dll"]
