# One image serves everything: the ASP.NET Core API, the SignalR hub and the
# built React app. Multi-stage builds keep the final image small: the Node and
# .NET SDKs are only used to build, and only the compiled output is copied into
# the runtime image.

# ---- 1. Build the React front end -------------------------------------------
FROM node:22-alpine AS web
WORKDIR /src/src/web
# Copy only the package files first so Docker can cache `npm ci` when only
# source files change.
COPY src/web/package.json src/web/package-lock.json ./
RUN npm ci
COPY src/web/ ./
# vite.config.ts writes the build into ../ButlerDidIt.Api/wwwroot
RUN npm run build

# ---- 2. Build and publish the .NET API ---------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY global.json ./
COPY src/ButlerDidIt.Game/ButlerDidIt.Game.csproj src/ButlerDidIt.Game/
COPY src/ButlerDidIt.Ai/ButlerDidIt.Ai.csproj src/ButlerDidIt.Ai/
COPY src/ButlerDidIt.Api/ButlerDidIt.Api.csproj src/ButlerDidIt.Api/
RUN dotnet restore src/ButlerDidIt.Api/ButlerDidIt.Api.csproj
COPY src/ButlerDidIt.Game/ src/ButlerDidIt.Game/
COPY src/ButlerDidIt.Ai/ src/ButlerDidIt.Ai/
COPY src/ButlerDidIt.Api/ src/ButlerDidIt.Api/
COPY --from=web /src/src/ButlerDidIt.Api/wwwroot src/ButlerDidIt.Api/wwwroot
RUN dotnet publish src/ButlerDidIt.Api/ButlerDidIt.Api.csproj -c Release -o /app --no-restore

# ---- 3. Runtime ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /app ./
COPY content ./content

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    Content__Root=content \
    DataProtection__KeysPath=/data/keys \
    Media__Root=/data/media

# Run as the image's built-in non-root user; give it the data folders that
# docker-compose mounts as volumes.
RUN mkdir -p /data/keys /data/media && chown -R $APP_UID /data
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "ButlerDidIt.Api.dll"]
