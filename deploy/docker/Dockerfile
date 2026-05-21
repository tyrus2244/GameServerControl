# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/GameServerControl.Shared/ ./GameServerControl.Shared/
COPY src/GameServerControl.Agent/ ./GameServerControl.Agent/
COPY Directory.Build.props ./
RUN dotnet publish GameServerControl.Agent/GameServerControl.Agent.csproj -c Release -r linux-x64 --self-contained false -o /app

# Runtime stage. ASP.NET runtime + 32-bit libs that steamcmd needs.
FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN dpkg --add-architecture i386 && \
    apt-get update && \
    apt-get install -y --no-install-recommends \
        ca-certificates curl tar lib32gcc-s1 lib32stdc++6 libsdl2-2.0-0:i386 && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# Volumes:
#   /config        -> appsettings.json, servers.json, tokens.json, SteamCMD cache
#   /gameservers   -> where Create Server installs land (bind-mount from host)
#   /backups       -> zip backups
VOLUME ["/config", "/gameservers", "/backups"]

# Agent reads these from environment + appsettings.json
ENV Agent__Bind=http://0.0.0.0:5099 \
    Agent__BackupRoot=/backups \
    DOTNET_ENVIRONMENT=Production

EXPOSE 5099 5443

ENTRYPOINT ["dotnet", "GameServerControl.Agent.dll"]
