# The dedicated server in a container. From the repository root:
#
#   docker build -f deploy/Dockerfile -t ksamp-server .
#   docker run -d --name ksamp -p 50100:50100/udp -v ksamp-data:/data \
#       -e KSAMP_NAME="My server" -e KSAMP_PASSWORD=secret ksamp-server

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
ENTRYPOINT ["dotnet", "/app/ksamp-server.dll", "--data", "/data"]
