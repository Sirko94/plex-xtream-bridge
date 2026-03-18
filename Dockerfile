# ─────────────────────────────────────────────────────────────────────────────
#  Stage 1 — Build
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Restore dependencies first (layer cache friendly)
COPY src/XtreamBridge/XtreamBridge.csproj ./XtreamBridge/
RUN dotnet restore ./XtreamBridge/XtreamBridge.csproj

# Copy source and publish
COPY src/XtreamBridge/ ./XtreamBridge/
RUN dotnet publish ./XtreamBridge/XtreamBridge.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        --self-contained false

# ─────────────────────────────────────────────────────────────────────────────
#  Stage 2 — Runtime (minimal alpine image)
# ─────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime

# Install tzdata so TimeZoneInfo works on Alpine
RUN apk add --no-cache tzdata

WORKDIR /app
COPY --from=build /app/publish .

# ── Volumes ───────────────────────────────────────────────────────────────────
# /config  — sync_state.json, logs, overridden appsettings.json
# /output  — generated .strm and .nfo files (mount this as a Plex library)
VOLUME ["/config", "/output"]

# ── Ports ─────────────────────────────────────────────────────────────────────
# 8080 — main HTTP port (HDHomeRun discovery, EPG, stream proxy)
EXPOSE 8080

# ── Environment defaults (override in docker-compose or -e flags) ─────────────
ENV ASPNETCORE_URLS="http://+:8080" \
    ASPNETCORE_ENVIRONMENT="Production" \
    XTREAM__Paths__Config="/config" \
    XTREAM__Paths__Output="/output"

ENTRYPOINT ["dotnet", "XtreamBridge.dll"]
