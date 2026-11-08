using System.Security.Cryptography;
using System.Text;

namespace DeskCal.Core;

/// <summary>
/// The password behind "content lock". Only a salted PBKDF2 hash is kept ("pbkdf2-sha256$iterations$salt$hash", Base64).
/// This guards the on-screen content (and the windows that show it); it does not encrypt the database file.
/// </summary>
public static class ContentLock
{
    public const int MinLength = 4, MaxLength = 128;
    private const int Iterations = 200_000, SaltBytes = 16, HashBytes = 32;
    private const string Scheme = "pbkdf2-sha256";

    /// <summary>The first problem with a candidate password, or null.</summary>
    public static string? Validate(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength) return $"비밀번호는 {MinLength}자 이상이어야 합니다.";
        if (password.Length > MaxLength) return $"비밀번호는 {MaxLength}자 이하여야 합니다.";
        return null;
    }

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC)), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>Constant-time comparison; a malformed or empty stored value never verifies.</summary>
    public static bool Verify(string? password, string? stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out int iterations) || iterations is < 1000 or > 10_000_000) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length == 0 || expected.Length == 0) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC)), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}
