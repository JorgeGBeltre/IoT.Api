# ==============================================================================
# Multi-stage Dockerfile for IoT SaaS Worker (.NET 8 Background Service)
# Standalone container build (context: .)
# ==============================================================================

# ------------------------------------------------------------------------------
# Stage 1: Build & Publish
# ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS builder
WORKDIR /src

RUN apk add --no-cache icu-libs

COPY IoT.Domain/IoT.Domain.csproj IoT.Domain/
COPY IoT.Protocols/IoT.Protocols.csproj IoT.Protocols/
COPY IoT.Application/IoT.Application.csproj IoT.Application/
COPY IoT.Infrastructure/IoT.Infrastructure.csproj IoT.Infrastructure/
COPY IoT.Worker/IoT.Worker.csproj IoT.Worker/

RUN dotnet restore IoT.Worker/IoT.Worker.csproj

COPY IoT.Domain/ IoT.Domain/
COPY IoT.Protocols/ IoT.Protocols/
COPY IoT.Application/ IoT.Application/
COPY IoT.Infrastructure/ IoT.Infrastructure/
COPY IoT.Worker/ IoT.Worker/

WORKDIR /src/IoT.Worker
RUN dotnet publish IoT.Worker.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ------------------------------------------------------------------------------
# Stage 2: Runtime Image (Lightweight Alpine)
# ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS runtime
WORKDIR /app

RUN apk add --no-cache icu-libs icu-data-full tzdata

RUN addgroup -g 1000 appgroup && \
    adduser -u 1000 -G appgroup -s /bin/sh -D appuser && \
    chown -R appuser:appgroup /app

COPY --from=builder --chown=appuser:appgroup /app/publish .

ENV DOTNET_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    TZ=America/Santo_Domingo \
    Worker__HeartbeatFile=/tmp/worker-heartbeat

USER appuser

# Liveness: el worker no expone HTTP; HeartbeatWorker refresca /tmp/worker-heartbeat con el
# epoch UNIX cada ~30s. Se marca unhealthy si el latido tiene más de 120s (bucle colgado).
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD test -f /tmp/worker-heartbeat && [ $(( $(date +%s) - $(cat /tmp/worker-heartbeat) )) -lt 120 ]

ENTRYPOINT ["dotnet", "IoT.Worker.dll"]
