# IoT SaaS Platform — Backend API & Ingestion Engine (IoT.Api)

Núcleo backend de la plataforma IoT SaaS, implementado bajo **Clean Architecture** en **.NET 8 (C#)**, con persistencia en **PostgreSQL 16 + PostGIS 3.4**, caché en **Redis 7**, mensajería **MQTT (MQTTnet 5 / Mosquitto)** y **SignalR** para streaming en vivo.

---

## 🏛️ Arquitectura de la Solución

- **IoT.Domain**: Entidades centrales (Device, DeviceCommand, Tenant, User, GeoPosition, etc.), invariantes de negocio y especificaciones.
- **IoT.Application**: Casos de uso orientados a slices (CQRS + MediatR), validaciones con FluentValidation y orquestación de eventos.
- **IoT.Infrastructure**: DbContext EF Core con NetTopologySuite, repositorios, cliente Redis, cliente MQTTnet, generador JWT y servicio SMTP.
- **IoT.Protocols**: Motor extensible de transporte (IDeviceTransport), catálogo de perfiles declarativos (IDeviceProfile) y adaptadores (GLP EKF, GPS Accident, Relés WiFi, SEEWORLD R12L-F TCP).
- **IoT.Api**: API RESTful ASP.NET Core, Swagger OpenAPI, rate limiting, middlewares de seguridad JWT y Hub de SignalR (/hubs/fleet).
- **IoT.Worker**: Servicio en segundo plano para suscripción MQTT (TelemetryIngestionWorker), ingestión de telemetría y gateway TCP binario (puerto 5023).
- **IoT.DeviceSimulator**: Simulador aislado para inyección controlada de telemetría sintética de prueba.
- **IoT.Tests**: Pruebas unitarias, pruebas de integración y pruebas de arquitectura (NetArchTest).

---

## ⚙️ Configuración (.env)

Copia .env.example a .env:

`ash
cp .env.example .env
`

Variables esenciales:

| Variable | Descripción | Valor por defecto |
|---|---|---|
| POSTGRES_DB | Nombre de base de datos | iot_platform_db |
| POSTGRES_USER | Usuario de PostgreSQL | iot_admin |
| POSTGRES_PASSWORD | Contraseña de PostgreSQL | Requerida |
| REDIS_PASSWORD | Contraseña requerida de Redis | Requerida |
| JWT_SECRET | Clave secreta simétrica (>= 32 chars) | Requerida |
| MQTT_PORT | Puerto TCP de broker MQTT | 1883 |
| TCP_GATEWAY_PORT | Puerto de gateway binario TCP | 5023 |
| API_PORT | Puerto de la API REST / SignalR | 5000 |

---

## 💻 Desarrollo local con .NET SDK

`ash
# Restaurar y compilar la solución
dotnet build IoT.slnx

# Ejecutar suite completa de pruebas
dotnet test IoT.slnx

# Iniciar la API Web
dotnet run --project IoT.Api/IoT.Api.csproj

# Iniciar el Worker de telemetría
dotnet run --project IoT.Worker/IoT.Worker.csproj
`

---

## 🐳 Despliegue con Docker (Independiente)

Levanta la pila completa de backend (Postgres + PostGIS, Redis, Mosquitto, API y Worker):

`ash
# Construir y levantar todos los servicios
docker compose up -d --build

# Ver logs en vivo
docker compose logs -f iot-api

# Ejecutar el simulador de telemetría opcionalmente
docker compose --profile simulation up -d iot-simulator

# Detener los servicios
docker compose down
`

### Endpoints principales:
- **Swagger UI**: http://localhost:5000/
- **Healthcheck**: http://localhost:5000/health
- **SignalR Fleet Hub**: http://localhost:5000/hubs/fleet
- **Gateway TCP**: 	cp://localhost:5023
