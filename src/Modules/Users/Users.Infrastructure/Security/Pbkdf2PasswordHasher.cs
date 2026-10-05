using System.Security.Cryptography;
using Users.Application.Security;

namespace Users.Infrastructure.Security;

/// <summary>How expensive password hashing is. Tests use a tiny cost; configuration can never go below <see cref="MinimumIterations"/>.</summary>
public sealed record PasswordHashingOptions(int Iterations = PasswordHashingOptions.DefaultIterations)
{
    /// <summary>OWASP guidance for PBKDF2-HMAC-SHA256.</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>Configured values below this are raised to it (the constructor of the hasher is the only place a lower cost is possible, for tests).</summary>
    public const int MinimumIterations = 100_000;

    public static PasswordHashingOptions FromConfiguration(int? configured)
        => new(Math.Max(configured ?? DefaultIterations, MinimumIterations));
}

/// <summary>
/// PBKDF2-HMAC-SHA256 through <see cref="Rfc2898DeriveBytes.Pbkdf2(string, byte[], int, HashAlgorithmName, int)"/> from the .NET base library
/// (no cryptography is invented here, no package is added). Every hash has its own 16-byte random salt. The stored form is
/// <c>PBKDF2-SHA256$iterations$salt$hash</c> (Base64), so the cost can be raised later and old hashes still verify - and are upgraded
/// on the next successful sign-in. Comparison is constant-time.
/// </summary>
public sealed class Pbkdf2PasswordHasher(PasswordHashingOptions options) : IPasswordHasher
{
    private const string Scheme = "PBKDF2-SHA256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int MaxAcceptedIterations = 5_000_000; // a corrupted/malicious stored value cannot make a sign-in run for minutes

    private readonly Lazy<string> _dummy = new(() => Create("dummy-password-nobody-knows", options.Iterations));

    public string DummyHash => _dummy.Value;

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Create(password, options.Iterations);
    }

    public PasswordVerification Verify(string password, string passwordHash)
    {
        if (password is null || !TryParse(passwordHash, out var iterations, out var salt, out var expected))
            return PasswordVerification.Failed;

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            return PasswordVerification.Failed;

        return iterations < options.Iterations ? PasswordVerification.SuccessRehashNeeded : PasswordVerification.Success;
    }

    private static string Create(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool TryParse(string? stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = [];
        hash = [];

        var parts = stored?.Split('$');
        if (parts is not { Length: 4 } || parts[0] != Scheme
            || !int.TryParse(parts[1], out iterations) || iterations < 1 || iterations > MaxAcceptedIterations)
            return false;

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length >= 8 && hash.Length is >= 16 and <= 64;
    }
}
