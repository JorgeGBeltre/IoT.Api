using System.Text;
using System.Threading.RateLimiting;
using IoT.Api.Common;
using IoT.Api.Hubs;
using IoT.Api.Services;
using IoT.Application;
using IoT.Application.Common.Interfaces;
using IoT.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Data Protection: persistir llaves fuera del contenedor en volumen dedicado (Item 23)
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "keys");
Directory.CreateDirectory(dataProtectionKeysPath);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
    .SetApplicationName("IoT.Nexus.Platform");

// 1. Core Architecture Layers
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// Gateways device-facing (broker MQTT embebido + gateway TCP GPS) SOLO en el proceso API.
builder.Services.AddDeviceFacingGateways();

// 2. Tenant Context & Real-time Notifications
builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddSingleton<IFleetNotificationService, SignalRFleetNotificationService>();

// Health Checks (Observability)
builder.Services.AddHealthChecks()
    .AddCheck<IoT.Api.Health.PostgresHealthCheck>("postgres")
    .AddCheck<IoT.Api.Health.RedisHealthCheck>("redis");

// 3. Controllers & JSON Options
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// 4. SignalR — detailed errors sólo en desarrollo (no filtrar excepciones al cliente en prod)
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

// 4b. ForwardedHeaders: la app corre detrás de un reverse proxy (nginx) que termina TLS.
//     Sin esto, Request.IsHttps=false (cookie sin Secure) y la IP real se pierde (auditoría/rate limit).
//     Sólo se confía en proxies de redes privadas (Docker/LAN) para evitar spoofing de X-Forwarded-*.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownNetworks.Add(new IPNetwork(System.Net.IPAddress.Parse("10.0.0.0"), 8));
    options.KnownNetworks.Add(new IPNetwork(System.Net.IPAddress.Parse("172.16.0.0"), 12));
    options.KnownNetworks.Add(new IPNetwork(System.Net.IPAddress.Parse("192.168.0.0"), 16));
});

// 5. CORS: orígenes configurables por entorno (Cors:AllowedOrigins). En dev, defaults locales;
//    en producción se EXIGE configurarlos (fail-fast) para no bloquear el login con credenciales.
string[]? corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
if (corsOrigins is null || corsOrigins.Length == 0)
{
    if (builder.Environment.IsDevelopment())
        corsOrigins = new[] { "http://localhost:3000", "http://127.0.0.1:3000", "http://localhost:5173" };
    else
        throw new InvalidOperationException("Critical Security Failure: 'Cors:AllowedOrigins' debe configurarse en producción (dominios del frontend).");
}
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy.WithOrigins(corsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Contrato de error unificado (ProblemDetails) para excepciones no controladas.
builder.Services.AddProblemDetails();

// E1-07: Rate Limiting particionado por IP real (requiere ForwardedHeaders).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Login / endpoints sensibles a fuerza bruta: 10/min por IP.
    options.AddPolicy("AuthRateLimit", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Ingesta HTTP de dispositivos (anónima): límite más amplio por IP.
    options.AddPolicy("DeviceIngest", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// 6. JWT Authentication & Fallback Authorization Policy
string jwtSecret = builder.Configuration["Jwt:SecretKey"]
    ?? throw new InvalidOperationException("Critical Security Failure: 'Jwt:SecretKey' configuration is missing.");

if (jwtSecret.Length < 32)
{
    throw new InvalidOperationException("Critical Security Failure: 'Jwt:SecretKey' must be at least 32 characters long.");
}

// En producción, rechazar los secretos de ejemplo/desarrollo (evita firmar tokens con una clave pública conocida).
if (!builder.Environment.IsDevelopment() &&
    (jwtSecret.Contains("DO-NOT-USE-IN-PROD", StringComparison.OrdinalIgnoreCase) ||
     jwtSecret.Contains("LocalDevelopmentOnly", StringComparison.OrdinalIgnoreCase)))
{
    throw new InvalidOperationException("Critical Security Failure: el 'Jwt:SecretKey' de desarrollo no puede usarse en producción. Rote el secreto a un valor único por despliegue.");
}

string jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "IoT-SaaS-Platform";
string jwtAudience = builder.Configuration["Jwt:Audience"] ?? "IoT-Frontend";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };

        // Allow SignalR to read access token from query string
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    // E0-02: Require authenticated user by default across all endpoints
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // Escribir firmware en hardware real exige un ROL explícito. Antes bastaba con estar
    // autenticado: cualquier usuario del tenant podía subir un .bin y flashear la flota.
    var firmwareRoles = IoT.Api.Common.AuthorizationPolicies.ResolveFirmwareManageRoles(builder.Configuration);
    options.AddPolicy(
        IoT.Api.Common.AuthorizationPolicies.FirmwareManage,
        policy => policy
            .RequireAuthenticatedUser()
            // No se usa RequireRole: compara el valor del claim distinguiendo mayúsculas y un
            // usuario guardado como "admin" quedaba denegado. Ver AuthorizationPolicies.HasAnyRole.
            .RequireAssertion(ctx => IoT.Api.Common.AuthorizationPolicies.HasAnyRole(ctx.User, firmwareRoles)));

    // Enviar un comando a un dispositivo también es accionamiento físico (apagar un relé),
    // pero es la operación del día a día: su conjunto de roles por defecto es más amplio.
    var commandRoles = IoT.Api.Common.AuthorizationPolicies.ResolveDeviceCommandRoles(builder.Configuration);
    options.AddPolicy(
        IoT.Api.Common.AuthorizationPolicies.DeviceCommand,
        policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => IoT.Api.Common.AuthorizationPolicies.HasAnyRole(ctx.User, commandRoles)));

    var deviceManageRoles = IoT.Api.Common.AuthorizationPolicies.ResolveDeviceManageRoles(builder.Configuration);
    options.AddPolicy(
        IoT.Api.Common.AuthorizationPolicies.DeviceManage,
        policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => IoT.Api.Common.AuthorizationPolicies.HasAnyRole(ctx.User, deviceManageRoles)));

    var tenantAdminRoles = IoT.Api.Common.AuthorizationPolicies.ResolveTenantAdminRoles(builder.Configuration);
    options.AddPolicy(
        IoT.Api.Common.AuthorizationPolicies.TenantAdmin,
        policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => IoT.Api.Common.AuthorizationPolicies.HasAnyRole(ctx.User, tenantAdminRoles)));

    // Operar la jerarquía Customer (ADR-013 E8.4b). Sólo controla el ROL: el recorte por subárbol lo
    // aplican la RLS de PostgreSQL y el filtro de EF, no esta política (ver AuthorizationPolicies.CustomerAdmin).
    var customerAdminRoles = IoT.Api.Common.AuthorizationPolicies.ResolveCustomerAdminRoles(builder.Configuration);
    options.AddPolicy(
        IoT.Api.Common.AuthorizationPolicies.CustomerAdmin,
        policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => IoT.Api.Common.AuthorizationPolicies.HasAnyRole(ctx.User, customerAdminRoles)));
});

// 7. Swagger / OpenAPI Documentation
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "IoT SaaS Platform API",
        Version = "v1",
        Description = "Enterprise SaaS API for GPS Fleet Tracking, Advanced Telemetry (GLP EKF), and Actuator Control."
    });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Reverse proxy: aplicar X-Forwarded-* ANTES que nada (esquema/IP reales).
app.UseForwardedHeaders();

// Cabeceras de seguridad base vía OnStarting, registrado ANTES del ExceptionHandler:
// así sobreviven al Response.Clear() de un 500 y cubren TODAS las respuestas (incl. 5xx).
// (La CSP se afina en el borde/staging por ser ownerDecision.)
app.Use((context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        return Task.CompletedTask;
    });
    return next();
});

// Contrato de error unificado para excepciones no controladas (sin filtrar stack traces).
app.UseExceptionHandler();

// Swagger SÓLO en desarrollo (no exponer la superficie del API en producción).
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "IoT SaaS Platform API v1");
        c.RoutePrefix = string.Empty; // Swagger UI at root "/"
    });
}

app.UseCors("AllowFrontend");
app.UseRateLimiter();

// E0-03: Middlewares order: Authentication -> Authorization -> TenantResolution
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantResolutionMiddleware>();

app.MapControllers();
app.MapHub<FleetHub>("/hubs/fleet");

// Health Check Endpoints (Observability)
app.MapHealthChecks("/health").AllowAnonymous();
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => true
}).AllowAnonymous();

// Migraciones + seeding + aprovisionamiento del rol RLS: TODO con la conexión ADMIN
// (iot_admin / superusuario), porque crear roles, poseer tablas y sembrar datos cross-tenant
// requieren privilegios que el rol de runtime (iot_app, NOBYPASSRLS) no tiene — ni debe tener.
// El runtime (ApplicationDbContext inyectado) usa ConnectionStrings:Postgres (iot_app), de modo
// que las peticiones quedan sujetas a RLS. ADR-003 D4/D6.
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var config = sp.GetRequiredService<IConfiguration>();

    var runtimeConn = config.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("Critical Configuration Error: 'ConnectionStrings:Postgres' no está configurada.");
    // Sin una cadena admin separada se reutiliza la de runtime (comportamiento previo, sin split de roles).
    var adminConn = config.GetConnectionString("PostgresAdmin") ?? runtimeConn;

    var adminOptions = new DbContextOptionsBuilder<IoT.Infrastructure.Persistence.ApplicationDbContext>()
        .UseNpgsql(adminConn, npgsql =>
        {
            npgsql.UseNetTopologySuite();
            npgsql.MigrationsAssembly(typeof(IoT.Infrastructure.Persistence.ApplicationDbContext).Assembly.FullName);
        })
        .Options;

    // Contexto admin SIN el interceptor de tenant: migrar/sembrar no deben tocar los GUC de RLS,
    // y el admin (superusuario) omite RLS por diseño para el seeding cross-tenant.
    await using var adminContext = new IoT.Infrastructure.Persistence.ApplicationDbContext(adminOptions, tenantContext: null);

    var seeder = new IoT.Infrastructure.Persistence.DatabaseSeeder(
        adminContext,
        sp.GetRequiredService<IPasswordHasher>(),
        sp.GetRequiredService<IDeviceApiKeyService>(),
        config,
        sp.GetRequiredService<IHostEnvironment>(),
        sp.GetRequiredService<ILogger<IoT.Infrastructure.Persistence.DatabaseSeeder>>());

    // SeedAsync aplica primero las migraciones (incluidas las políticas RLS) y luego siembra.
    await seeder.SeedAsync();

    // Ya migrado (las tablas existen), provisiona/rota el rol de runtime iot_app + sus grants.
    await IoT.Infrastructure.Persistence.RlsRoleBootstrapper.EnsureAppRoleAsync(
        adminContext,
        runtimeConn,
        adminConn,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("IoT.Infrastructure.Persistence.RlsRoleBootstrapper"));

    // Guarda de arranque (E8.4c): en PRODUCCIÓN exige que el aislamiento por RLS esté forzado
    // (Enforce=true y rol de runtime NOBYPASSRLS/NOSUPERUSER) o la app NO arranca (fail-fast). Fuera
    // de producción sólo avisa (dev/test pueden correr con el rol admin). Se consulta por la conexión
    // de runtime el estado del rol real con el que la app se conectará.
    await IoT.Infrastructure.Persistence.RlsStartupGuard.EnsureIsolationEnforcedAsync(
        runtimeConn,
        app.Environment.IsProduction(),
        config.GetValue<bool>("Platform:Rls:Enforce", false),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("IoT.Infrastructure.Persistence.RlsStartupGuard"));
}

app.Run();
