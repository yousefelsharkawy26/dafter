using System;
using System.Security.Cryptography;
using Daftar.Infrastructure.Security;
using Xunit;

namespace Daftar.Domain.Tests;

/// <summary>
/// Regression tests for two authentication-integrity holes found in the audit.
///
/// Both are "everything looks fine until it isn't" bugs: the production code path is short and
/// reads correctly, and no other test exercises the malformed inputs that trigger them.
/// </summary>
public class PasswordHasherHardeningTests
{
    /// <summary>
    /// A stored hash whose digest segment is empty — what a truncated write, a partial database
    /// restore, or a half-finished password reset leaves behind.
    ///
    /// Pbkdf2 was called with <c>outputLength: expected.Length == 0</c>, which does not throw, and
    /// then <c>FixedTimeEquals(empty, empty)</c> returned true. Every password authenticated.
    /// </summary>
    [Theory]
    [InlineData("admin")]
    [InlineData("")]
    [InlineData("anything at all")]
    [InlineData("1234")]
    public void Verify_TruncatedDigest_RejectsEveryPassword(string password)
    {
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var truncated = $"pbkdf2-sha256$210000${salt}$";

        Assert.False(PasswordHasher.Verify(password, truncated));
    }

    [Fact]
    public void Verify_TruncatedDigest_RejectsEvenTheRealPassword()
    {
        // Guards the specific regression: it must not degrade into "accepts if the password
        // happens to be the one that was hashed".
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var truncated = $"pbkdf2-sha256$210000${salt}$";

        Assert.False(PasswordHasher.Verify("admin", truncated));
        Assert.False(PasswordHasher.Verify("admin", $"pbkdf2-sha256$210000$"));
    }

    /// <summary>
    /// The iteration count is data the method obeys unconditionally. An absurd value made every
    /// login attempt burn CPU for seconds, and a zero or negative one is not a real scheme either.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(99_999)]
    public void Verify_UnusableIterationCount_IsRejected(int iterations)
    {
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var digest = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var stored = $"pbkdf2-sha256${iterations}${salt}${digest}";

        Assert.False(PasswordHasher.Verify("admin", stored));
    }

    [Fact]
    public void Verify_AbsurdIterationCount_IsRejected()
    {
        // Would otherwise be obeyed, pinning the thread for seconds on a single login attempt.
        // The bound must reject it before any PBKDF2 work happens, so this stays instant.
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var digest = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var stored = $"pbkdf2-sha256${int.MaxValue}${salt}${digest}";

        Assert.False(PasswordHasher.Verify("admin", stored));
    }

    [Fact]
    public void Verify_SaltShorterThanTheScheme_Rejects()
    {
        // A one-byte salt silently destroys the whole point of salting.
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(1));
        var digest = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var stored = $"pbkdf2-sha256$210000${salt}${digest}";

        Assert.False(PasswordHasher.Verify("admin", stored));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2-sha256$210000$only-three-parts")]
    [InlineData("pbkdf2-sha256$210000$not-base64!$not-base64!")]
    [InlineData("bcrypt$210000$c2FsdA==$aGFzaA==")]
    public void Verify_MalformedStoredHash_IsRejected(string stored)
    {
        Assert.False(PasswordHasher.Verify("admin", stored));
    }

    // ---- the happy path must survive all of the above ----

    [Fact]
    public void Verify_CorrectPassword_StillAuthenticates()
    {
        var stored = PasswordHasher.Hash("correct horse battery staple");

        Assert.True(PasswordHasher.Verify("correct horse battery staple", stored));
        Assert.False(PasswordHasher.Verify("wrong password", stored));
    }

    [Fact]
    public void Hash_SamePasswordTwice_ProducesDifferentHashes()
    {
        // Per-hash random salt. If this ever collapses, identical passwords become linkable
        // across the user table.
        var first = PasswordHasher.Hash("same");
        var second = PasswordHasher.Hash("same");

        Assert.NotEqual(first, second);
        Assert.True(PasswordHasher.Verify("same", first));
        Assert.True(PasswordHasher.Verify("same", second));
    }

    [Fact]
    public void Hash_UsesTheDocumentedIterationCount_WhichVerifyAccepts()
    {
        // The bounds in Verify must not reject a hash that Hash() itself produced.
        var stored = PasswordHasher.Hash("round trip");

        var parts = stored.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.True(int.TryParse(parts[1], out var iterations));
        Assert.InRange(iterations, 100_000, 2_000_000);
        Assert.True(PasswordHasher.Verify("round trip", stored));
    }
}