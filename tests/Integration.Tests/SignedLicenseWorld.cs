using System.Security.Cryptography;
using System.Text.Json;
using Client.Licensing.Domain;
using Client.Licensing.Infrastructure;
using Licensing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Client.Host.Hosting;
using Tests.Common.Security;

namespace Integration.Tests;

internal sealed class ClockModule(TimeProvider clock) : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services) => services.AddSingleton(clock);
}

/// <summary>A real signed license (signed with a throw-away key, trusted through configuration) bound to a known installation.</summary>
internal sealed class SignedLicenseWorld : IDisposable
{
    private static readonly string[] EnvironmentKeys =
    [
        "GENERICPOS_Licensing__StorageDirectory", "GENERICPOS_Licensing__TrustedKeys__0__KeyId", "GENERICPOS_Licensing__TrustedKeys__0__PublicKey"
    ];

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Guid _installationId = Guid.NewGuid();

    public SignedLicenseWorld(DateTimeOffset now, TestClock clock)
    {
        Clock = clock;
        Directory = Path.Combine(Path.GetTempPath(), "genericpos-lic-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Environment.SetEnvironmentVariable(EnvironmentKeys[0], Directory);
        Environment.SetEnvironmentVariable(EnvironmentKeys[1], "it-key");
        Environment.SetEnvironmentVariable(EnvironmentKeys[2], Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()));
        File.WriteAllText(Path.Combine(Directory, "installation.json"),
            JsonSerializer.Serialize(new InstallationIdentity(_installationId, now), LicenseSerializer.Options));
    }

    public TestClock Clock { get; }
    public string Directory { get; }

    public void Issue(DateTimeOffset now, DateTimeOffset validUntil, params string[] modules)
        => Issue(now, validUntil, LicenseStatusClaim.Active, validUntil, validUntil, modules);

    /// <summary>Signs and stores a license with an explicit status (Revoked/Suspended) and lease/grace window.</summary>
    public void Issue(DateTimeOffset now, DateTimeOffset validUntil, LicenseStatusClaim status, DateTimeOffset leaseUntil, DateTimeOffset graceUntil, params string[] modules)
        => File.WriteAllText(Path.Combine(Directory, "license.json"), LicenseSerializer.Serialize(Sign(now, validUntil, status, leaseUntil, graceUntil, modules)));

    /// <summary>A license for this installation signed with this world's trusted key, without storing it (what a license server would answer).</summary>
    public SignedLicense Sign(DateTimeOffset now, DateTimeOffset validUntil, LicenseStatusClaim status, DateTimeOffset leaseUntil, DateTimeOffset graceUntil, params string[] modules)
    {
        var payload = new LicensePayload(
            Guid.NewGuid(), "customer-1", _installationId, "genericpos", 1, now, now.AddDays(-1), validUntil,
            leaseUntil, graceUntil, status, modules, [], "integration test", "it-key");
        var bytes = LicenseSerializer.SerializePayloadBytes(payload);
        var signature = _key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new SignedLicense(LicenseSerializer.ToPayloadText(bytes), "it-key", LicenseSigning.Algorithm, Convert.ToBase64String(signature));
    }

    public IEnumerable<IHostingModule> HostModules() => [new LicensingHostingModule(), new ClockModule(Clock)];

    public void Dispose()
    {
        foreach (var key in EnvironmentKeys) Environment.SetEnvironmentVariable(key, null);
        _key.Dispose();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
