using System.Text;
using System.Threading.RateLimiting;
using IoT.Api.Common;
using IoT.Api.Hubs;
using IoT.Api.Services;
using IoT.Application;
using IoT.Application.Common.Interfaces;
using IoT.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

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

// Automated database schema creation and initial seeding
using (var scope = app.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<IoT.Infrastructure.Persistence.DatabaseSeeder>();
    await seeder.SeedAsync();
}

app.Run();
