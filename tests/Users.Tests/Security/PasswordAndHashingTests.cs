using Users.Application.Security;
using Users.Domain.Entities;
using Users.Domain.ValueObjects;
using Users.Infrastructure.Security;

namespace Users.Tests.Security;

public sealed class PasswordPolicyTests
{
    private static readonly PasswordPolicy Policy = PasswordPolicy.Default;

    [Fact]
    public void A_long_unpredictable_password_is_accepted()
        => Assert.True(Policy.Validate("correct horse battery", "ann").IsSuccess);

    [Theory]
    [InlineData(null, "Users.Password.Required")]
    [InlineData("", "Users.Password.Required")]
    [InlineData("   ", "Users.Password.Required")]
    [InlineData("short1!", "Users.Password.TooShort")]
    [InlineData("aaaaaaaaaaaa", "Users.Password.TooSimple")]
    [InlineData("1111111111", "Users.Password.TooSimple")]
    [InlineData("Password123", "Users.Password.TooCommon")]
    [InlineData("QWERTY-123", "Users.Password.TooCommon")]
    [InlineData("1234567890", "Users.Password.TooCommon")]
    public void Weak_or_malformed_passwords_are_rejected(string? password, string code)
    {
        var result = Policy.Validate(password, "ann");

        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error.Code);
    }

    [Fact]
    public void A_password_that_contains_the_username_is_rejected()
        => Assert.Equal("Users.Password.ContainsUsername", Policy.Validate("my-jonathan-secret", "Jonathan").Error.Code);

    [Fact]
    public void Very_long_passwords_are_rejected_instead_of_hashed()
        => Assert.Equal("Users.Password.TooLong", Policy.Validate(new string('a', 129) + "bcdef", "x").Error.Code);

    [Fact]
    public void Configuration_can_raise_but_never_lower_the_floor()
    {
        Assert.Equal(PasswordPolicy.MinimumLengthFloor, new PasswordPolicy(2, 50).Normalized().MinimumLength);
        Assert.Equal(14, new PasswordPolicy(14, 50).Normalized().MinimumLength);
        Assert.Equal(PasswordPolicy.AbsoluteMaximumLength, new PasswordPolicy(10, 5000).Normalized().MaximumLength);
        Assert.True(new PasswordPolicy(1, 1).Validate("abcdef1", null).IsFailure); // the 8-character floor cannot be configured away
    }

    [Fact]
    public void Spaces_and_unicode_are_fine_in_a_passphrase()
        => Assert.True(Policy.Validate("пароль со пробелами 123", "ann").IsSuccess);
}

public sealed class LockoutPolicyTests
{
    [Fact]
    public void The_floor_and_a_positive_duration_are_always_enforced()
    {
        var normalized = new LockoutPolicy(1, TimeSpan.Zero).Normalized();

        Assert.Equal(LockoutPolicy.MaxFailedAttemptsFloor, normalized.MaxFailedAttempts);
        Assert.Equal(LockoutPolicy.DefaultDuration, normalized.Duration);
    }
}

public sealed class UserCredentialTests
{
    private static readonly DateTime Now = new(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);
    private static readonly LockoutPolicy Policy = new(3, TimeSpan.FromMinutes(10));

    private static UserCredential New() => UserCredential.Create(UserId.New(), "hash", Now).Value;

    [Fact]
    public void Create_requires_a_user_and_a_hash()
    {
        Assert.True(UserCredential.Create(UserId.Empty, "h", Now).IsFailure);
        Assert.True(UserCredential.Create(UserId.New(), " ", Now).IsFailure);
        Assert.True(UserCredential.Create(UserId.New(), new string('x', UserCredential.MaxHashLength + 1), Now).IsFailure);
    }

    [Fact]
    public void The_account_locks_on_the_configured_failure_and_only_then()
    {
        var c = New();

        Assert.False(c.RegisterFailure(Now, Policy));
        Assert.False(c.RegisterFailure(Now, Policy));
        Assert.False(c.IsLocked(Now));
        Assert.True(c.RegisterFailure(Now, Policy));
        Assert.True(c.IsLocked(Now));
        Assert.Equal(Now.AddMinutes(10), c.LockedUntil);
    }

    [Fact]
    public void A_lock_expires_and_the_count_restarts()
    {
        var c = New();
        for (var i = 0; i < 3; i++) c.RegisterFailure(Now, Policy);

        var later = Now.AddMinutes(10);
        Assert.False(c.IsLocked(later));
        Assert.False(c.RegisterFailure(later, Policy)); // first failure of a fresh count, not an immediate relock
        Assert.Equal(1, c.FailedAttempts);
        Assert.Null(c.LockedUntil);
    }

    [Fact]
    public void Failures_while_locked_change_nothing()
    {
        var c = New();
        for (var i = 0; i < 3; i++) c.RegisterFailure(Now, Policy);
        var until = c.LockedUntil;

        Assert.False(c.RegisterFailure(Now.AddMinutes(1), Policy));

        Assert.Equal(until, c.LockedUntil);
        Assert.Equal(3, c.FailedAttempts);
    }

    [Fact]
    public void Success_clears_failures_and_setting_a_password_unlocks()
    {
        var c = New();
        c.RegisterFailure(Now, Policy);
        c.RegisterSuccess(Now.AddMinutes(1));
        Assert.Equal(0, c.FailedAttempts);
        Assert.Equal(Now.AddMinutes(1), c.LastSignInAt);

        for (var i = 0; i < 3; i++) c.RegisterFailure(Now, Policy);
        Assert.True(c.IsLocked(Now));
        Assert.True(c.SetPassword("new-hash", Now.AddMinutes(2), mustChangePassword: true).IsSuccess);

        Assert.False(c.IsLocked(Now));
        Assert.True(c.MustChangePassword);
        Assert.Equal("new-hash", c.PasswordHash);
    }

    [Fact]
    public void Upgrading_the_hash_changes_nothing_else()
    {
        var c = New();
        c.RegisterFailure(Now, Policy);

        c.UpgradeHash("stronger");

        Assert.Equal("stronger", c.PasswordHash);
        Assert.Equal(Now, c.PasswordChangedAt);
        Assert.Equal(1, c.FailedAttempts);
    }
}

public sealed class Pbkdf2PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new(new PasswordHashingOptions(1000));

    [Fact]
    public void The_hash_is_self_describing_and_never_contains_the_password()
    {
        var hash = _hasher.Hash("correct horse battery");

        Assert.StartsWith("PBKDF2-SHA256$1000$", hash);
        Assert.DoesNotContain("correct", hash);
        Assert.DoesNotContain("battery", hash);
        Assert.Equal(4, hash.Split('$').Length);
    }

    [Fact]
    public void Every_hash_has_its_own_salt()
    {
        var a = _hasher.Hash("same password here");
        var b = _hasher.Hash("same password here");

        Assert.NotEqual(a, b);
        Assert.NotEqual(a.Split('$')[2], b.Split('$')[2]);
        Assert.Equal(PasswordVerification.Success, _hasher.Verify("same password here", a));
        Assert.Equal(PasswordVerification.Success, _hasher.Verify("same password here", b));
    }

    [Fact]
    public void The_right_password_verifies_and_a_wrong_one_does_not()
    {
        var hash = _hasher.Hash("correct horse battery");

        Assert.Equal(PasswordVerification.Success, _hasher.Verify("correct horse battery", hash));
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify("correct horse batterY", hash));
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify("", hash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plaintext-password")]
    [InlineData("PBKDF2-SHA256$1000$salt")]
    [InlineData("PBKDF2-SHA256$abc$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("PBKDF2-SHA256$1000$not-base64!$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("MD5$1000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("PBKDF2-SHA256$999999999$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("PBKDF2-SHA256$0$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void A_malformed_stored_hash_simply_fails_verification(string stored)
        => Assert.Equal(PasswordVerification.Failed, _hasher.Verify("whatever password", stored));

    [Fact]
    public void A_hash_made_with_a_lower_cost_verifies_but_asks_to_be_upgraded()
    {
        var old = new Pbkdf2PasswordHasher(new PasswordHashingOptions(500)).Hash("correct horse battery");

        Assert.Equal(PasswordVerification.SuccessRehashNeeded, _hasher.Verify("correct horse battery", old));
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify("wrong", old));
    }

    [Fact]
    public void The_dummy_hash_is_valid_and_never_matches_a_real_password()
    {
        Assert.Equal(PasswordVerification.Failed, _hasher.Verify("correct horse battery", _hasher.DummyHash));
        Assert.Equal(_hasher.DummyHash, _hasher.DummyHash);
    }

    [Fact]
    public void Configuration_can_never_lower_the_cost_below_the_minimum()
    {
        Assert.Equal(PasswordHashingOptions.MinimumIterations, PasswordHashingOptions.FromConfiguration(10).Iterations);
        Assert.Equal(PasswordHashingOptions.DefaultIterations, PasswordHashingOptions.FromConfiguration(null).Iterations);
        Assert.Equal(900_000, PasswordHashingOptions.FromConfiguration(900_000).Iterations);
    }
}
