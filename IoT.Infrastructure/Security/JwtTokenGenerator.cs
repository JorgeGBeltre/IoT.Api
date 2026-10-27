using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using IoT.Application.Common.Interfaces;
using IoT.Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace IoT.Infrastructure.Security;

public sealed class JwtTokenGenerator : IJwtTokenGenerator
{
    private readonly IConfiguration _configuration;

    public JwtTokenGenerator(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public AuthTokenResult GenerateToken(User user, Guid tenantId, Guid? customerId = null, string? customerPath = null)
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
        // servidor (no se confía al cliente). Si el usuario no tiene nodo, se OMITEN ambos claims:
        // un token sin customer no restringe el subárbol (ve todo su tenant), lo que mantiene la
        // activación NO DISRUPTIVA para usuarios y tokens anteriores a E8.2.
        if (customerId.HasValue && customerId.Value != Guid.Empty)
        {
            claims.Add(new Claim("customer_id", customerId.Value.ToString()));
            if (!string.IsNullOrWhiteSpace(customerPath))
            {
                claims.Add(new Claim("customer_path", customerPath));
            }
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
            expiresAt);
    }
}
