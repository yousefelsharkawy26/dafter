using System.Security.Cryptography;
using System.Text;

namespace Daftar.Infrastructure.Security;

/// <summary>
/// PBKDF2-SHA256 password hashing, using the framework's own primitives — no extra dependency.
/// Stored format: "pbkdf2-sha256$&lt;iterations&gt;$&lt;salt-b64&gt;$&lt;hash-b64&gt;".
/// </summary>
public static class PasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int DefaultIterations = 210_000;

    /// <summary>
    /// Accepted range for the iteration count stored in a hash. The lower bound rejects rows
    /// written by some future weaker scheme; the upper bound stops a corrupt or edited row from
    /// pinning the CPU for seconds on every login attempt, since the count is stored data that
    /// this method would otherwise obey unconditionally.
    /// </summary>
    private const int MinIterations = 100_000;
    private const int MaxIterations = 2_000_000;

    private const string Prefix = "pbkdf2-sha256";

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);

        return $"{Prefix}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash))
            return false;

        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Prefix)
            return false;

        if (!int.TryParse(parts[1], out var iterations)
            || iterations is < MinIterations or > MaxIterations)
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        // A stored digest of the wrong length is corruption, not a valid hash. Without this, a
        // truncated row ("pbkdf2-sha256$210000$<salt>$", as produced by a partial restore or a
        // half-finished reset) derives a zero-byte digest and then compares two empty arrays:
        // FixedTimeEquals(empty, empty) is true, so every password would authenticate.
        if (salt.Length < SaltBytes || expected.Length != HashBytes)
            return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // Fixed-time comparison so a wrong password cannot be narrowed down by timing.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}