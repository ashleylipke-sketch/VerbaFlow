# VerbaFlow as one container, for running online (Azure Container Apps). See docs/going-online.md.

# 1. The web pages
FROM node:22-bookworm-slim AS web
WORKDIR /src/web
COPY web/package.json web/package-lock.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

# 2. The server
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server
WORKDIR /src
COPY VerbaFlow.sln ./
COPY src/ src/
RUN dotnet publish src/VerbaFlow.Api/VerbaFlow.Api.csproj -c Release -r linux-x64 --self-contained false -o /app

# 3. What runs: the server, the web pages, ffmpeg (audio clean-up) and what the Speech SDK needs on Linux.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg ca-certificates libssl3t64 libasound2t64 curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=server /app ./
COPY --from=web /src/src/VerbaFlow.Api/wwwroot ./wwwroot
# Recordings, letterheads and the speaker models live in /data (a storage share online).
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    VerbaFlow__DataDir=/data
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s CMD curl -fs http://localhost:8080/api/health || exit 1
ENTRYPOINT ["dotnet", "VerbaFlow.Api.dll"]
