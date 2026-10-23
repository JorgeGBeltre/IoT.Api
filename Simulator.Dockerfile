# ==============================================================================
# Multi-stage Dockerfile for IoT SaaS Device Simulator (.NET 8 Worker)
# Standalone container build (context: .)
# ==============================================================================

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS builder
WORKDIR /src

RUN apk add --no-cache icu-libs

COPY IoT.Domain/IoT.Domain.csproj IoT.Domain/
COPY IoT.Protocols/IoT.Protocols.csproj IoT.Protocols/
COPY IoT.Application/IoT.Application.csproj IoT.Application/
COPY IoT.Infrastructure/IoT.Infrastructure.csproj IoT.Infrastructure/
COPY IoT.DeviceSimulator/IoT.DeviceSimulator.csproj IoT.DeviceSimulator/

RUN dotnet restore IoT.DeviceSimulator/IoT.DeviceSimulator.csproj

COPY IoT.Domain/ IoT.Domain/
COPY IoT.Protocols/ IoT.Protocols/
COPY IoT.Application/ IoT.Application/
COPY IoT.Infrastructure/ IoT.Infrastructure/
COPY IoT.DeviceSimulator/ IoT.DeviceSimulator/

WORKDIR /src/IoT.DeviceSimulator
RUN dotnet publish IoT.DeviceSimulator.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS runtime
WORKDIR /app

RUN apk add --no-cache icu-libs tzdata

RUN addgroup -g 1000 appgroup && \
    adduser -u 1000 -G appgroup -s /bin/sh -D appuser && \
    chown -R appuser:appgroup /app

COPY --from=builder --chown=appuser:appgroup /app/publish .

ENV DOTNET_ENVIRONMENT=Development \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    TZ=America/Santo_Domingo

USER appuser

ENTRYPOINT ["dotnet", "IoT.DeviceSimulator.dll"]
