using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace IoT.Infrastructure.Security;

public sealed class JwtTokenGenerator : IJwtTokenGenerator
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<JwtTokenGenerator>? _logger;

    // El logger es opcional para no romper los constructores directos de los tests; en producción
    // lo inyecta el contenedor DI.
    public JwtTokenGenerator(IConfiguration configuration, ILogger<JwtTokenGenerator>? logger = null)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public AuthTokenResult GenerateToken(
        User user,
        Guid tenantId,
        Guid? customerId = null,
        string? customerPath = null)
    {
        return GenerateToken(user, tenantId, customerId, customerPath, null);
    }

    public AuthTokenResult GenerateToken(
        User user,
        Guid tenantId,
        Guid? customerId,
        string? customerPath,
        IEnumerable<string>? permissions)
    {
        string secretKey = _configuration["Jwt:SecretKey"]
            ?? throw new InvalidOperationException("Critical Security Failure: 'Jwt:SecretKey' configuration is missing.");

        if (secretKey.Length < 32)
        {
            throw new InvalidOperationException("Critical Security Failure: 'Jwt:SecretKey' must be at least 32 characters (256 bits) long.");
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.UniqueName, user.Username),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("tenant_id", tenantId.ToString()),
            new(ClaimTypes.Role, user.RoleName),
            new("full_name", user.FullName),
            new("must_change_password", user.MustChangePassword ? "true" : "false")
        };

        // Jerarquía multinivel (ADR-013): el nodo del usuario viaja como claim firmado y el
        // middleware lo lee con anti-spoofing (igual que tenant_id). El Path se resuelve en el
        // servidor (no se confía al cliente).
        //
        // EMISIÓN SOLIDARIA (E8.4a, punto 4): customer_id y customer_path se emiten AMBOS o NINGUNO.
        //   - Sin nodo (customerId null) → se omiten los dos: token no acotado por subárbol (ve todo
        //     su tenant). NO DISRUPTIVO para usuarios/tokens anteriores a E8.2.
        //   - Con nodo y path válido → se emiten los dos.
        //   - Con nodo pero SIN path (null/vacío) → es un ESTADO INVÁLIDO del propio emisor (el
        //     llamador no resolvió el Path desde la tabla customers). Un token con customer_id y sin
        //     customer_path hace que el TenantResolutionMiddleware responda 403 en CADA request
        //     (fail-closed), bloqueando al usuario por un token mal formado por nosotros. FAIL-CLOSED
        //     aquí también: se registra y se LANZA en vez de emitir un token inconsistente (o de
        //     omitir customer_id, lo que degradaría a un sub-customer a ver todo el tenant).
        if (customerId.HasValue && customerId.Value != Guid.Empty)
        {
            if (string.IsNullOrWhiteSpace(customerPath))
            {
                _logger?.LogError(
                    "Critical Security Failure: se intentó emitir un token con customer_id {CustomerId} para el usuario {UserId} sin un customer_path resuelto. Token NO emitido.",
                    customerId.Value, user.Id);
                throw new InvalidOperationException(
                    "Critical Security Failure: no se puede emitir un token con customer_id pero sin customer_path (estado inconsistente del emisor). Resuelva el Path del nodo antes de generar el token.");
            }

            claims.Add(new Claim("customer_id", customerId.Value.ToString()));
            claims.Add(new Claim("customer_path", customerPath));
        }

        var permList = permissions?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList()
            ?? new List<string>();
        foreach (var perm in permList)
        {
            claims.Add(new Claim("permission", perm));
        }

        int expirationHours = 2;
        if (int.TryParse(_configuration["Jwt:ExpirationHours"], out int h) && h > 0)
        {
            expirationHours = h;
        }
        else if (int.TryParse(_configuration["Jwt:ExpirationDays"], out int d) && d > 0)
        {
            expirationHours = d * 24;
        }

        var expiresAt = DateTime.UtcNow.AddHours(expirationHours);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = expiresAt,
            Issuer = _configuration["Jwt:Issuer"] ?? "IoT-SaaS-Platform",
            Audience = _configuration["Jwt:Audience"] ?? "IoT-Frontend",
            SigningCredentials = credentials
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);
        string encodedToken = tokenHandler.WriteToken(token);

        return new AuthTokenResult(
            encodedToken,
            tenantId,
            user.Id,
            user.Username,
            user.Email,
            user.RoleName,
            user.MustChangePassword,
            expiresAt,
            permList);
    }
}
