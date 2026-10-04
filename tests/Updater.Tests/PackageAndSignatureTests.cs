using System.IO.Compression;
using System.Text;
using Client.Updater.Domain;
using Platform.Core.Modules;
using Security.Es256;
using Security.Es256.Signing;
using Tools.ModulePackager;
using Updates.Contracts;
using Updates.Package;

namespace Updater.Tests;

public sealed class PackageAndSignatureTests : IDisposable
{
    private readonly UpdateWorld _w = new();

    public void Dispose() => _w.Dispose();

    // ------------------------------------------------------------------ package format

    [Fact]
    public void ValidPackage_CanBeCreated_Read_AndVerified()
    {
        var path = _w.PublishModule("catalog", "1.3.0");

        var read = PackageReader.Open(path);
        Assert.True(read.IsSuccess);
        using (read.Contents)
            Assert.Equal(["config/settings.json", "module.dll"], read.Contents!.PayloadPaths.OrderBy(x => x).ToArray());

        var manifest = UpdateWorld.ReadManifest(path);
        Assert.Equal(PackageType.Module, manifest.PackageType);
        Assert.Equal("catalog", manifest.TargetId);
        Assert.Equal("1.3.0", manifest.Version);
        Assert.Equal(UpdateWorld.KeyId, manifest.KeyId);
        Assert.Equal(PayloadDigest.Compute(manifest.Files), manifest.PayloadHash);

        var verified = _w.PackageVerifier.VerifyPackage(path);
        Assert.True(verified.IsSuccess, verified.IsFailure ? verified.Error.ToString() : null);
        Assert.Equal(VersionRelation.Upgrade, verified.Value.Relation);
    }

    [Fact]
    public void Publishing_IsDeterministic_ForTheSameInputs()
    {
        var payload = _w.PayloadDir("det");
        var spec = _w.ModuleSpec("catalog", "1.3.0", payload);
        var id = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var draft = ModulePackager.CreateDraft(spec).Draft!;
        var a = Tools.UpdatePublisher.UpdatePublisher.Publish(draft, _w.Signer, Path.Combine(_w.Dir, "a.gpkg"), at, id);
        var b = Tools.UpdatePublisher.UpdatePublisher.Publish(draft, _w.Signer, Path.Combine(_w.Dir, "b.gpkg"), at, id);

        // The payload part is byte-identical; only the (randomised) ECDSA signature may differ.
        Assert.Equal(PackageManifestSerializer.SerializeManifestBytes(a.Manifest!), PackageManifestSerializer.SerializeManifestBytes(b.Manifest!));
        Assert.Equal(a.Manifest!.PayloadHash, b.Manifest!.PayloadHash);
        Assert.Equal(ReadPayload(a.PackagePath!), ReadPayload(b.PackagePath!));
    }

    private static Dictionary<string, string> ReadPayload(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Where(e => e.FullName.StartsWith("payload/"))
            .ToDictionary(e => e.FullName, e => { using var s = e.Open(); return Sha256Hex.Compute(s); });
    }

    [Fact]
    public void PackageThatIsNotAZip_IsRejected()
    {
        var path = Path.Combine(_w.Dir, "junk.gpkg");
        File.WriteAllText(path, "this is not a zip");

        var result = _w.PackageVerifier.VerifyPackage(path);

        Assert.True(result.IsFailure);
        Assert.Equal(UpdateErrorCodes.InvalidPackage, result.Error.Code);
    }

    [Fact]
    public void MissingPackageFile_IsRejectedAsNotFound()
        => Assert.Equal(UpdateErrorCodes.NotFound, _w.PackageVerifier.VerifyPackage(Path.Combine(_w.Dir, "nope.gpkg")).Error.Code);

    [Fact]
    public void PackageWithoutManifest_IsRejected()
    {
        var path = _w.PublishModule("catalog", "1.3.0");
        var tampered = UpdateWorld.Repack(path, e => e.Remove(PackageFormat.ManifestEntry));

        Assert.Equal(UpdateErrorCodes.InvalidPackage, _w.PackageVerifier.VerifyPackage(tampered).Error.Code);
    }

    [Fact]
    public void MalformedManifestEnvelope_IsRejected()
    {
        var path = _w.PublishModule("catalog", "1.3.0");
        var tampered = UpdateWorld.Repack(path, e => e[PackageFormat.ManifestEntry] = Encoding.UTF8.GetBytes("{ not json"));

        Assert.Equal(UpdateErrorCodes.InvalidPackage, _w.PackageVerifier.VerifyPackage(tampered).Error.Code);
    }

    [Fact]
    public void SignedButMalformedManifest_IsRejected()
    {
        // Correctly signed garbage: the signature checks out but the content is not a manifest.
        var garbage = Encoding.UTF8.GetBytes("{\"packageId\": 5, \"nonsense\": true");
        var envelope = new SignedPackageManifest(Convert.ToBase64String(garbage), UpdateWorld.KeyId, _w.Signer.Algorithm,
            Convert.ToBase64String(_w.Signer.Sign(garbage)));
        var path = _w.PublishModule("catalog", "1.3.0");
        var tampered = UpdateWorld.Repack(path, e => e[PackageFormat.ManifestEntry] = Encoding.UTF8.GetBytes(PackageManifestSerializer.SerializeEnvelope(envelope)));

        Assert.Equal(UpdateErrorCodes.InvalidPackage, _w.PackageVerifier.VerifyPackage(tampered).Error.Code);
    }

    [Fact]
    public void UnsupportedPackageType_IsRejected()
    {
        var m = UpdateWorld.ManifestFor("catalog", "1.3.0") with { PackageType = (PackageType)99 };
        var path = UpdateWorld.WriteSignedPackage(Path.Combine(_w.Dir, "bad-type.gpkg"), m, _w.Signer, ("module.dll", "x"));

        var result = _w.PackageVerifier.VerifyPackage(path);

        Assert.Equal(UpdateErrorCodes.InvalidPackage, result.Error.Code);
    }

    [Fact]
    public void UnsupportedPackageSchemaVersion_IsRejected()
    {
        var m = UpdateWorld.ManifestFor("catalog", "1.3.0") with { SchemaVersion = 99 };
        var path = UpdateWorld.WriteSignedPackage(Path.Combine(_w.Dir, "schema.gpkg"), m, _w.Signer, ("module.dll", "x"));

        Assert.Equal(UpdateErrorCodes.Incompatible, _w.PackageVerifier.VerifyPackage(path).Error.Code);
    }

    [Theory]
    [InlineData("core", PackageType.Module)]        // module package aimed at "core"
    [InlineData("catalog", PackageType.Core)]       // core package aimed at a module
    [InlineData("Catalog", PackageType.Module)]     // not normalised
    [InlineData("", PackageType.Module)]
    [InlineData("bad id!", PackageType.Module)]
    public void InvalidPackageIdentity_IsRejected(string target, PackageType type)
    {
        var m = UpdateWorld.ManifestFor(target, "1.3.0", type);
        var path = UpdateWorld.WriteSignedPackage(Path.Combine(_w.Dir, "identity.gpkg"), m, _w.Signer, ("module.dll", "x"));

        Assert.Equal(UpdateErrorCodes.InvalidPackage, _w.PackageVerifier.VerifyPackage(path).Error.Code);
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("1.2.3.4")]
    [InlineData("-1.0.0")]
    public void InvalidPackageVersion_IsRejected(string version)
    {
        var m = UpdateWorld.ManifestFor("catalog", version);
        var path = UpdateWorld.WriteSignedPackage(Path.Combine(_w.Dir, "ver.gpkg"), m, _w.Signer, ("module.dll", "x"));

        Assert.Equal(UpdateErrorCodes.InvalidPackage, _w.PackageVerifier.VerifyPackage(path).Error.Code);
    }

    [Fact]
    public void ZipSlipEntry_IsRejected_BeforeAnythingIsExtracted()
    {
        var path = _w.PublishModule("catalog", "1.3.0");
        var tampered = UpdateWorld.Repack(path, e => e["payload/../../evil.dll"] = [1, 2, 3]);

        var result = _w.PackageVerifier.VerifyPackage(tampered);

        Assert.Equal(UpdateErrorCodes.InvalidPackage, result.Error.Code);
        Assert.False(File.Exists(Path.Combine(_w.Dir, "evil.dll")));
    }

    [Fact]
    public void UnexpectedTopLevelEntry_IsRejected()
    {
        var path = _w.PublishModule("catalog", "1.3.0");
        var tampered = UpdateWorld.Repack(path, e => e["install.ps1"] = Encoding.UTF8.GetBytes("Write-Host pwned"));

        Assert.Equal(UpdateErrorCodes.InvalidPackage, _w.PackageVerifier.VerifyPackage(tampered).Error.Code);
    }

    [Fact]
    public void UnlistedPayloadFile_HiddenInTheArchive_IsRejected()
    {
        var path = _w.PublishModule("catalog", "1.3.0");
        var tampered = UpdateWorld.Repack(path, e => e["payload/extra.dll"] = [9, 9, 9]);

        var result = _w.PackageVerifier.VerifyPackage(tampered);

        Assert.Equal(UpdateErrorCodes.InvalidPackage, result.Error.Code);
        Assert.Contains("unlisted", result.Error.Description);
    }

    [Fact]
    public void MissingListedPayloadFile_IsRejected()
    {
        var path = _w.PublishModule("catalog", "1.3.0");
        var tampered = UpdateWorld.Repack(path, e => e.Remove("payload/module.dll"));

        Assert.Equal(UpdateErrorCodes.InvalidPackage, _w.PackageVerifier.VerifyPackage(tampered).Error.Code);
    }

    [Fact]
    public void SignedPackageListingAScript_IsRejected_EvenWithAValidSignature()
    {
        var files = new[] { ("module.dll", "x"), ("install.ps1", "Write-Host pwned") };
        var m = UpdateWorld.ManifestFor("catalog", "1.3.0", files: files);
        var path = UpdateWorld.WriteSignedPackage(Path.Combine(_w.Dir, "script.gpkg"), m, _w.Signer, files);

        var result = _w.PackageVerifier.VerifyPackage(path);

        Assert.Equal(UpdateErrorCodes.InvalidPackage, result.Error.Code);
        Assert.Contains("install.ps1", result.Error.Description);
    }

    [Fact]
    public void ModulePackage_WithNativeExecutable_IsRejected_ButCorePackageMayContainOne()
    {
        var files = new[] { ("module.dll", "x"), ("tool.exe", "MZ") };
        var module = UpdateWorld.WriteSignedPackage(Path.Combine(_w.Dir, "mod-exe.gpkg"),
            UpdateWorld.ManifestFor("catalog", "1.3.0", files: files), _w.Signer, files);
        var core = UpdateWorld.WriteSignedPackage(Path.Combine(_w.Dir, "core-exe.gpkg"),
            UpdateWorld.ManifestFor("core", "1.1.0", PackageType.Core, files), _w.Signer, files);

        Assert.Equal(UpdateErrorCodes.InvalidPackage, _w.PackageVerifier.VerifyPackage(module).Error.Code);
        Assert.True(_w.PackageVerifier.VerifyPackage(core).IsSuccess);
    }

    // ------------------------------------------------------------------ packager rejects before signing

    [Fact]
    public void Packager_Rejects_MissingOrEmptyPayload()
    {
        var empty = Path.Combine(_w.Dir, "empty");
        Directory.CreateDirectory(empty);

        Assert.False(ModulePackager.CreateDraft(_w.ModuleSpec("catalog", "1.0.0", Path.Combine(_w.Dir, "missing"))).IsSuccess);
        Assert.False(ModulePackager.CreateDraft(_w.ModuleSpec("catalog", "1.0.0", empty)).IsSuccess);
    }

    [Fact]
    public void Packager_Rejects_ScriptsAndModuleExecutables()
    {
        var withScript = _w.PayloadDir("script", ("module.dll", "x"), ("post-install.bat", "del *"));
        var withExe = _w.PayloadDir("exe", ("module.dll", "x"), ("helper.exe", "MZ"));

        Assert.False(ModulePackager.CreateDraft(_w.ModuleSpec("catalog", "1.0.0", withScript)).IsSuccess);
        Assert.False(ModulePackager.CreateDraft(_w.ModuleSpec("catalog", "1.0.0", withExe)).IsSuccess);
        Assert.True(ModulePackager.CreateDraft(_w.CoreSpec("1.1.0", withExe)).IsSuccess);   // core may carry an exe
    }

    [Theory]
    [InlineData("Catalog", "1.0.0", "1.0.0")]
    [InlineData("bad id", "1.0.0", "1.0.0")]
    [InlineData("core", "1.0.0", "1.0.0")]          // module package may not claim "core"
    [InlineData("catalog", "x.y", "1.0.0")]
    [InlineData("catalog", "1.0.0", "garbage")]
    public void Packager_Rejects_InvalidIdentityOrVersions(string target, string version, string minHost)
    {
        var spec = _w.ModuleSpec(target, version, _w.PayloadDir("p")) with { MinimumHostVersion = minHost };

        Assert.False(ModulePackager.CreateDraft(spec).IsSuccess);
    }

    [Fact]
    public void Packager_Rejects_InvalidDependencies_SelfDependency_AndBadMigration()
    {
        var payload = _w.PayloadDir("p");
        var baseSpec = _w.ModuleSpec("catalog", "1.0.0", payload);

        Assert.False(ModulePackager.CreateDraft(baseSpec with { Dependencies = [new PackageDependency("inventory", "~", "1.0.0")] }).IsSuccess);
        Assert.False(ModulePackager.CreateDraft(baseSpec with { Dependencies = [new PackageDependency("inventory", ">=", "nope")] }).IsSuccess);
        Assert.False(ModulePackager.CreateDraft(baseSpec with { Dependencies = [new PackageDependency("catalog", ">=", "1.0.0")] }).IsSuccess);
        Assert.False(ModulePackager.CreateDraft(baseSpec with { Migration = new PackageMigration(3, 2, true) }).IsSuccess);
        Assert.False(ModulePackager.CreateDraft(_w.CoreSpec("1.1.0", payload) with { Migration = new PackageMigration(1, 2, true) }).IsSuccess);
        Assert.True(ModulePackager.CreateDraft(baseSpec with { Migration = new PackageMigration(1, 2, true) }).IsSuccess);
    }

    [Fact]
    public void Publisher_RefusesToSign_APayloadThatChangedAfterValidation()
    {
        var payload = _w.PayloadDir("change");
        var draft = ModulePackager.CreateDraft(_w.ModuleSpec("catalog", "1.0.0", payload)).Draft!;
        File.WriteAllText(Path.Combine(payload, "module.dll"), "tampered after validation");

        var result = Tools.UpdatePublisher.UpdatePublisher.Publish(draft, _w.Signer, Path.Combine(_w.Dir, "x.gpkg"));

        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(Path.Combine(_w.Dir, "x.gpkg")));
    }

    [Fact]
    public void PackageContents_NeverContainPrivateKeyMaterial()
    {
        var path = _w.PublishModule("catalog", "1.3.0");
        var all = new StringBuilder();
        using (var zip = ZipFile.OpenRead(path))
            foreach (var e in zip.Entries)
            {
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                all.Append(Encoding.UTF8.GetString(ms.ToArray()));
            }

        Assert.DoesNotContain("PRIVATE KEY", all.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ hashing

    [Fact]
    public void ModifiedPayloadByte_IsDetected_AsHashMismatch()
    {
        var path = _w.PublishModule("catalog", "1.3.0");
        var tampered = UpdateWorld.Repack(path, e => { e["payload/module.dll"][0] ^= 0x01; });

        var result = _w.PackageVerifier.VerifyPackage(tampered);

        Assert.True(result.IsFailure);
        Assert.Equal(UpdateErrorCodes.HashMismatch, result.Error.Code);
    }

    [Fact]
    public void PayloadReplacedWithSameLengthContent_IsDetected()
    {
        var path = _w.PublishModule("catalog", "1.3.0");
        var tampered = UpdateWorld.Repack(path, e => e["payload/module.dll"] = Encoding.UTF8.GetBytes(new string('Z', e["payload/module.dll"].Length)));

        Assert.Equal(UpdateErrorCodes.HashMismatch, _w.PackageVerifier.VerifyPackage(tampered).Error.Code);
    }

    [Fact]
    public void SignedManifestWithWrongPayloadHash_IsRejected()
    {
        var m = UpdateWorld.ManifestFor("catalog", "1.3.0") with { PayloadHash = new string('a', 64) };
        var path = UpdateWorld.WriteSignedPackage(Path.Combine(_w.Dir, "badhash.gpkg"), m, _w.Signer, ("module.dll", "x"));

        Assert.Equal(UpdateErrorCodes.HashMismatch, _w.PackageVerifier.VerifyPackage(path).Error.Code);
    }

    [Fact]
    public void SignedManifestWithWrongFileHash_IsRejected()
    {
        var m = UpdateWorld.ManifestFor("catalog", "1.3.0", tweakFiles: files => files[0] = files[0] with { Sha256 = new string('b', 64) });
        var path = UpdateWorld.WriteSignedPackage(Path.Combine(_w.Dir, "badfilehash.gpkg"), m, _w.Signer, ("module.dll", "x"));

        Assert.Equal(UpdateErrorCodes.HashMismatch, _w.PackageVerifier.VerifyPackage(path).Error.Code);
    }

    [Fact]
    public void PayloadDigest_IsOrderIndependent_AndSensitiveToEveryField()
    {
        var a = new PackageFile("a.dll", new string('1', 64), 10);
        var b = new PackageFile("b.dll", new string('2', 64), 20);

        Assert.Equal(PayloadDigest.Compute([a, b]), PayloadDigest.Compute([b, a]));
        Assert.NotEqual(PayloadDigest.Compute([a, b]), PayloadDigest.Compute([a with { Path = "c.dll" }, b]));
        Assert.NotEqual(PayloadDigest.Compute([a, b]), PayloadDigest.Compute([a with { Length = 11 }, b]));
        Assert.NotEqual(PayloadDigest.Compute([a, b]), PayloadDigest.Compute([a with { Sha256 = new string('3', 64) }, b]));
        Assert.NotEqual(PayloadDigest.Compute([a, b]), PayloadDigest.Compute([a]));
    }

    // ------------------------------------------------------------------ signatures

    [Fact]
    public void ModifiedSignedManifest_IsRejected_AsSignatureInvalid()
    {
        var path = _w.PublishModule("catalog", "1.3.0");
        var envelope = UpdateWorld.ReadEnvelope(path);
        var manifest = UpdateWorld.ReadManifest(path) with { Version = "9.9.9" };   // attacker bumps the version
        var forged = envelope with { Manifest = PackageManifestSerializer.ToManifestText(PackageManifestSerializer.SerializeManifestBytes(manifest)) };
        var tampered = UpdateWorld.Repack(path, e => e[PackageFormat.ManifestEntry] = Encoding.UTF8.GetBytes(PackageManifestSerializer.SerializeEnvelope(forged)));

        var result = _w.PackageVerifier.VerifyPackage(tampered);

        Assert.Equal(UpdateErrorCodes.SignatureInvalid, result.Error.Code);
    }

    [Fact]
    public void ModifiedSignature_IsRejected()
    {
        var path = _w.PublishModule("catalog", "1.3.0");
        var envelope = UpdateWorld.ReadEnvelope(path);
        var sig = Convert.FromBase64String(envelope.Signature);
        sig[0] ^= 0xFF;
        var tampered = UpdateWorld.Repack(path, e => e[PackageFormat.ManifestEntry] =
            Encoding.UTF8.GetBytes(PackageManifestSerializer.SerializeEnvelope(envelope with { Signature = Convert.ToBase64String(sig) })));

        Assert.Equal(UpdateErrorCodes.SignatureInvalid, _w.PackageVerifier.VerifyPackage(tampered).Error.Code);
    }

    [Fact]
    public void UnknownSigningKey_IsRejected_AsUntrusted()
    {
        using var stranger = Es256Signer.GenerateEphemeral("stranger-key");
        var path = _w.Publish(_w.ModuleSpec("catalog", "1.3.0", _w.PayloadDir("s")), stranger, "stranger.gpkg");

        var result = _w.PackageVerifier.VerifyPackage(path);

        Assert.Equal(UpdateErrorCodes.UnknownSigningKey, result.Error.Code);
    }

    [Fact]
    public void WrongKey_WithTrustedKeyId_IsRejected_AsInvalidSignature()
    {
        using var impostor = Es256Signer.GenerateEphemeral(UpdateWorld.KeyId);   // same KeyId, different key
        var path = _w.Publish(_w.ModuleSpec("catalog", "1.3.0", _w.PayloadDir("s")), impostor, "impostor.gpkg");

        Assert.Equal(UpdateErrorCodes.SignatureInvalid, _w.PackageVerifier.VerifyPackage(path).Error.Code);
    }

    [Fact]
    public void KeyRotation_PackagesFromEitherTrustedKey_AreAccepted_AndRetiredKeysRejected()
    {
        using var next = Es256Signer.GenerateEphemeral("pub-2");
        var oldPackage = _w.PublishModule("catalog", "1.3.0");
        var newPackage = _w.Publish(_w.ModuleSpec("catalog", "1.4.0", _w.PayloadDir("n")), next, "rotated.gpkg");

        // Rotation window: both keys trusted.
        _w.Rebuild([_w.Signer.ToTrustedKey(), next.ToTrustedKey()]);
        Assert.True(_w.PackageVerifier.VerifyPackage(oldPackage).IsSuccess);
        Assert.True(_w.PackageVerifier.VerifyPackage(newPackage).IsSuccess);

        // After retiring the old key, its packages no longer verify.
        _w.Rebuild([next.ToTrustedKey()]);
        Assert.Equal(UpdateErrorCodes.UnknownSigningKey, _w.PackageVerifier.VerifyPackage(oldPackage).Error.Code);
        Assert.True(_w.PackageVerifier.VerifyPackage(newPackage).IsSuccess);
    }

    [Fact]
    public void NoTrustedKeys_FailsClosed()
    {
        var path = _w.PublishModule("catalog", "1.3.0");
        _w.Rebuild([]);

        Assert.Equal(UpdateErrorCodes.UnknownSigningKey, _w.PackageVerifier.VerifyPackage(path).Error.Code);
    }

    [Fact]
    public void KeyIdInsideManifest_MustMatchTheEnvelope()
    {
        var m = UpdateWorld.ManifestFor("catalog", "1.3.0") with { KeyId = "someone-else" };
        var path = UpdateWorld.WriteSignedPackage(Path.Combine(_w.Dir, "keyid.gpkg"), m, _w.Signer, ("module.dll", "x"));

        Assert.Equal(UpdateErrorCodes.InvalidPackage, _w.PackageVerifier.VerifyPackage(path).Error.Code);
    }

    [Fact]
    public void UnsupportedAlgorithm_IsRejected()
    {
        var path = _w.PublishModule("catalog", "1.3.0");
        var envelope = UpdateWorld.ReadEnvelope(path) with { Algorithm = "none" };
        var tampered = UpdateWorld.Repack(path, e => e[PackageFormat.ManifestEntry] = Encoding.UTF8.GetBytes(PackageManifestSerializer.SerializeEnvelope(envelope)));

        Assert.Equal(UpdateErrorCodes.SignatureInvalid, _w.PackageVerifier.VerifyPackage(tampered).Error.Code);
    }

    [Fact]
    public void Signature_IsVerifiedBeforeAnyManifestContentIsTrusted()
    {
        // A forged manifest that would otherwise fail identity checks must be reported as a SIGNATURE failure first.
        var path = _w.PublishModule("catalog", "1.3.0");
        var envelope = UpdateWorld.ReadEnvelope(path);
        var forged = UpdateWorld.ReadManifest(path) with { PackageType = (PackageType)42, TargetId = "!!" };
        var tampered = UpdateWorld.Repack(path, e => e[PackageFormat.ManifestEntry] = Encoding.UTF8.GetBytes(PackageManifestSerializer.SerializeEnvelope(
            envelope with { Manifest = PackageManifestSerializer.ToManifestText(PackageManifestSerializer.SerializeManifestBytes(forged)) })));

        Assert.Equal(UpdateErrorCodes.SignatureInvalid, _w.PackageVerifier.VerifyPackage(tampered).Error.Code);
    }

    [Fact]
    public void RuntimeModuleManifest_HasNoPackageSecurityFields()
    {
        var names = typeof(IModuleManifest).GetProperties().Select(p => p.Name).ToList();

        Assert.DoesNotContain("PackageHash", names);
        Assert.DoesNotContain("Signature", names);
        Assert.DoesNotContain("SigningKeyId", names);
        Assert.DoesNotContain("KeyId", names);
        Assert.DoesNotContain("PayloadHash", names);
    }
}
