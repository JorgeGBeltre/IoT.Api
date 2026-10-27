using System.Security.Cryptography;

namespace IoT.Application.Users;

/// <summary>
/// Genera una contraseña TEMPORAL fuerte con el CSPRNG del sistema
/// (<see cref="RandomNumberGenerator"/>, el mismo generador seguro que usan los secretos de
/// dispositivo y los OTP). Garantiza al menos una mayúscula, una minúscula, un dígito y un carácter
/// especial, de modo que satisface la política de complejidad del
/// <c>BCryptPasswordHasher.ValidateComplexity</c> y el usuario puede iniciar sesión con ella y
/// cambiarla por el flujo de clave temporal. La contraseña viaja en claro UNA vez en la respuesta;
/// nunca se persiste ni se registra en claro (sólo su hash BCrypt).
/// </summary>
public static class TemporaryPasswordGenerator
{
    // Alfabetos sin caracteres ambiguos (I/l/1/O/0) para una clave que a veces se dicta a mano.
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnpqrstuvwxyz";
    private const string Digits = "23456789";
    // Subconjunto del especial aceptado por ValidateComplexity ([!@#$%^&*(),.?":{}|<>]).
    private const string Special = "!@#$%^&*?";

    public static string Generate(int length = 16)
    {
        if (length < 12) length = 12;

        string all = Upper + Lower + Digits + Special;
        var chars = new char[length];

        // Una de cada categoría requerida por la política (el resto, del alfabeto completo).
        chars[0] = Pick(Upper);
        chars[1] = Pick(Lower);
        chars[2] = Pick(Digits);
        chars[3] = Pick(Special);
        for (int i = 4; i < length; i++)
            chars[i] = Pick(all);

        Shuffle(chars);
        return new string(chars);
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];

    private static void Shuffle(char[] chars)
    {
        // Fisher–Yates con índices del CSPRNG (sin sesgo): que la posición de los caracteres
        // garantizados no sea predecible.
        for (int i = chars.Length - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
    }
}
