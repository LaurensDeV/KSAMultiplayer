# The dedicated server in a container. From the repository root:
#
#   docker build -f deploy/Dockerfile -t ksamp-server .
#   docker run -dit --name ksamp -p 50100:50100/udp -v ksamp-data:/data ksamp-server
#
# Settings are /data/ksamp-server.toml, in the volume so they outlive the container, written with
# every default on the first start. Edit it and restart, or set them from the console.
#
# No -e KSAMP_ here, for the reason the unit has no Environment= line: a variable overrides the
# file for good, so the name set that way is one the console can write down and never apply.
#
# -it, because the server's commands are read from standard input and a container started without a
# terminal has none: docker attach ksamp types into it, Ctrl+P Ctrl+Q leaves it running.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Protocol/ src/Protocol/
COPY src/Server/ src/Server/
COPY global.json Directory.Build.props ./
RUN dotnet publish src/Server/KSAMultiplayer.Server.csproj -c Release -o /out -p:DebugType=none

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /data
COPY --from=build /out /app
RUN useradd --system --home /data ksamp && chown ksamp /data
USER ksamp
EXPOSE 50100/udp
VOLUME /data
ENTRYPOINT ["dotnet", "/app/ksamp-server.dll", "--config", "/data/ksamp-server.toml", "--data", "/data"]
