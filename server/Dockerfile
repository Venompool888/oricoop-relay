# OriCoop relay server.
# Build (from the repo root):  docker build -t oricoop-relay server
# Run:    docker run -d --name oricoop-relay --restart unless-stopped -p 47020:47020/udp oricoop-relay
# Keep the player log in a volume (see docs/PLAYER-LOG.md):
#         docker run -d --name oricoop-relay --restart unless-stopped -p 47020:47020/udp \
#           -v oricoop-relay-logs:/logs -e ORICOOP_PLAYER_LOG_DIR=/logs oricoop-relay
# Players: relay address = <server-ip-or-domain>:47020 (the host gets a room code, the friend types it).
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY RelayServer.csproj RelayServer.cs RelayPlayerLog.cs ./
RUN dotnet publish RelayServer.csproj -c Release -o /out --nologo

FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine
WORKDIR /app
COPY --from=build /out ./
# Folder for the optional player-log volume, writable by the non-root user.
RUN mkdir -p /logs && chown app /logs
# Run as the image's non-root user.
USER app
EXPOSE 47020/udp
ENTRYPOINT ["dotnet", "RelayServer.dll"]
CMD ["47020"]
