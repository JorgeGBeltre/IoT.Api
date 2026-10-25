# ==============================================================================
# Multi-stage Dockerfile for IoT SaaS API (.NET 8 Web API)
# Standalone container build (context: .)
# Reference pattern: C:\Users\Jorge\Desktop\Ecf\Ecf.api\Dockerfile
# ==============================================================================

# ------------------------------------------------------------------------------
# Stage 1: Build & Publish
# ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS builder
WORKDIR /src

RUN apk add --no-cache icu-libs

# Copiar csproj de cada capa interna para cache de capas Docker
COPY IoT.Domain/IoT.Domain.csproj IoT.Domain/
COPY IoT.Protocols/IoT.Protocols.csproj IoT.Protocols/
COPY IoT.Application/IoT.Application.csproj IoT.Application/
COPY IoT.Infrastructure/IoT.Infrastructure.csproj IoT.Infrastructure/
COPY IoT.Api/IoT.Api.csproj IoT.Api/

# Restaurar dependencias NuGet
RUN dotnet restore IoT.Api/IoT.Api.csproj

# Copiar todo el codigo fuente de los proyectos dependientes
COPY IoT.Domain/ IoT.Domain/
COPY IoT.Protocols/ IoT.Protocols/
COPY IoT.Application/ IoT.Application/
COPY IoT.Infrastructure/ IoT.Infrastructure/
COPY IoT.Api/ IoT.Api/

# Compilar y publicar la aplicacion en modo Release
WORKDIR /src/IoT.Api
RUN dotnet publish IoT.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ------------------------------------------------------------------------------
# Stage 2: Runtime Image (Lightweight Alpine)
# ------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

RUN apk add --no-cache icu-libs icu-data-full tzdata wget curl

# Se crean TODOS los directorios de datos persistentes en la imagen ANTES de bajar a
# appuser, incluido /app/storage (almacen OTA de firmware). Es imprescindible: cuando un
# named volume VACIO se monta sobre un directorio que existe en la imagen, Docker lo
# inicializa copiando el contenido Y la propiedad de ese directorio. Si /app/storage no
# existe en la imagen, Docker crea el punto de montaje como root y el volumen queda
# root:root 755, de modo que appuser (uid 1000) no puede crear /app/storage/firmware y la
# subida OTA falla con "Permission denied". Creandolo aqui (y con el chown -R de abajo), un
# volumen nuevo hereda uid 1000 y es escribible. OJO: un volumen YA creado como root
# conserva su propiedad; hay que recrearlo al desplegar este arreglo (ver docker-compose.yml).
RUN mkdir -p /app/data /app/logs /app/storage/firmware && \
    addgroup -g 1000 appgroup && \
    adduser -u 1000 -G appgroup -s /bin/sh -D appuser && \
    chown -R appuser:appgroup /app

COPY --from=builder --chown=appuser:appgroup /app/publish .

ENV ASPNETCORE_URLS=http://+:5000 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    TZ=America/Santo_Domingo

EXPOSE 5000

USER appuser

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD wget --no-verbose --tries=1 --spider http://localhost:5000/health || exit 1

ENTRYPOINT ["dotnet", "IoT.Api.dll"]
