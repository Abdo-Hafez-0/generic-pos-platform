using System.Text;
using Client.Security;
using Platform.Application.Abstractions.Security;

namespace Security.Tests.DataProtection;

/// <summary>The real Windows DPAPI implementation. These tests are only meaningful on Windows, which is the platform of the desktop app.</summary>
public sealed class DpapiSecretProtectorTests
{
    private static ISecretProtector? Create() => OperatingSystem.IsWindows() ? new DpapiSecretProtector() : null;

    [Fact]
    public void A_secret_round_trips_and_the_protected_form_does_not_contain_it()
    {
        if (Create() is not { } protector) return;
        var secret = Encoding.UTF8.GetBytes("installation-1f6c0f0e-7b19-4a8e-9a34-4f9a0b5f1c11");

        var blob = protector.Protect(secret, "tests.purpose");

        Assert.False(blob.AsSpan().IndexOf(secret) >= 0, "the plaintext must not appear in the protected blob");
        Assert.True(protector.TryUnprotect(blob, "tests.purpose", out var opened));
        Assert.Equal(secret, opened);
    }

    [Fact]
    public void A_blob_only_opens_for_the_purpose_it_was_made_for()
    {
        if (Create() is not { } protector) return;
        var blob = protector.Protect(Encoding.UTF8.GetBytes("secret"), "purpose.a");

        Assert.False(protector.TryUnprotect(blob, "purpose.b", out var other));
        Assert.Empty(other);
    }

    [Fact]
    public void A_changed_blob_never_opens_to_different_content()
    {
        if (Create() is not { } protector) return;
        var secret = Encoding.UTF8.GetBytes("secret that must not be alterable");
        var blob = protector.Protect(secret, "tests.purpose");

        // DPAPI does not authenticate every header byte, so a flipped bit in an unauthenticated field may still open - but then it opens to the
        // SAME content. What must never happen is an altered blob that opens to something else; flipping the sealed region must fail.
        var failed = 0;
        for (var i = 0; i < blob.Length; i++)
        {
            var altered = (byte[])blob.Clone();
            altered[i] ^= 0x01;
            if (protector.TryUnprotect(altered, "tests.purpose", out var opened))
                Assert.Equal(secret, opened);
            else
                failed++;
        }

        Assert.True(failed > blob.Length * 8 / 10, $"most single-bit changes are detected ({failed} of {blob.Length})");
    }

    [Fact]
    public void Garbage_and_truncated_data_fail_cleanly()
    {
        if (Create() is not { } protector) return;

        Assert.False(protector.TryUnprotect([], "tests.purpose", out _));
        Assert.False(protector.TryUnprotect(new byte[] { 1, 2, 3, 4 }, "tests.purpose", out _));
        var blob = protector.Protect(Encoding.UTF8.GetBytes("secret"), "tests.purpose");
        Assert.False(protector.TryUnprotect(blob.AsSpan(0, blob.Length / 2), "tests.purpose", out _));
    }

    [Fact]
    public void A_purpose_is_required()
    {
        if (Create() is not { } protector) return;

        Assert.ThrowsAny<ArgumentException>(() => protector.Protect(new byte[] { 1 }, " "));
    }

    [Fact]
    public void The_hosting_module_registers_the_protector_on_windows_only()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        new ClientSecurityHostingModule().RegisterServices(new Microsoft.Extensions.Hosting.HostBuilderContext(new Dictionary<object, object>()), services);

        Assert.Equal(OperatingSystem.IsWindows(), services.Any(d => d.ServiceType == typeof(ISecretProtector)));
    }
}
