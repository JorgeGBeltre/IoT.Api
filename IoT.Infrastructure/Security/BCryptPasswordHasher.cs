using System.Text.RegularExpressions;
using IoT.Application.Common.Interfaces;

namespace IoT.Infrastructure.Security;

public sealed class BCryptPasswordHasher : IPasswordHasher
{
    public string Hash(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11);
    }

    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(hash))
            return false;

        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch
        {
            return false;
        }
    }

    public void ValidateComplexity(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new InvalidOperationException("La contraseña debe tener al menos 8 caracteres.");

        if (!Regex.IsMatch(password, @"[A-Z]"))
            throw new InvalidOperationException("La contraseña debe contener al menos una letra mayúscula.");

        if (!Regex.IsMatch(password, @"[a-z]"))
            throw new InvalidOperationException("La contraseña debe contener al menos una letra minúscula.");

        if (!Regex.IsMatch(password, @"[0-9]"))
            throw new InvalidOperationException("La contraseña debe contener al menos un número.");

        if (!Regex.IsMatch(password, @"[!@#$%^&*(),.?"":{}|<>]"))
            throw new InvalidOperationException("La contraseña debe contener al menos un carácter especial (@, #, $, etc.).");
    }
}
