using Platform.Core.Modules;
using Platform.ModuleContract.Tests.Helpers;

namespace Platform.ModuleContract.Tests;

/// <summary>
/// Tests for IModuleManifest contract correctness and ModuleId/FeatureId consistency.
/// Verifies that the contract behaves correctly when implemented by a concrete type.
/// </summary>
public sealed class ModuleManifestTests
{
    // -----------------------------------------------------------------------
    // ModuleId consistency
    // -----------------------------------------------------------------------

    [Theory(DisplayName = "ModuleId: normalized to lowercase")]
    [InlineData("Catalog",   "catalog")]
    [InlineData("INVENTORY", "inventory")]
    [InlineData("Sales-POS", "sales-pos")]
    public void ModuleId_NormalizesToLowercase(string input, string expected)
    {
        var id = new ModuleId(input);
        Assert.Equal(expected, id.Value);
    }

    [Theory(DisplayName = "ModuleId: invalid values throw ArgumentException")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ModuleId_InvalidValue_Throws(string? input)
    {
        Assert.Throws<ArgumentException>(() => new ModuleId(input!));
    }

    [Fact(DisplayName = "ModuleId: two equal IDs are structurally equal")]
    public void ModuleId_EqualValues_AreEqual()
    {
        var a = new ModuleId("catalog");
        var b = new ModuleId("CATALOG"); // normalized
        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    // -----------------------------------------------------------------------
    // FeatureId consistency
    // -----------------------------------------------------------------------

    [Theory(DisplayName = "FeatureId: normalized to lowercase")]
    [InlineData("Accounting.General-Ledger", "accounting.general-ledger")]
    [InlineData("INVENTORY.BATCH-TRACKING", "inventory.batch-tracking")]
    public void FeatureId_NormalizesToLowercase(string input, string expected)
    {
        var id = new FeatureId(input);
        Assert.Equal(expected, id.Value);
    }

    [Theory(DisplayName = "FeatureId: invalid values throw ArgumentException")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FeatureId_InvalidValue_Throws(string? input)
    {
        Assert.Throws<ArgumentException>(() => new FeatureId(input!));
    }

    [Fact(DisplayName = "FeatureId: two equal IDs are structurally equal")]
    public void FeatureId_EqualValues_AreEqual()
    {
        var a = new FeatureId("accounting.general-ledger");
        var b = new FeatureId("ACCOUNTING.GENERAL-LEDGER");
        Assert.Equal(a, b);
    }

    // -----------------------------------------------------------------------
    // IModuleManifest contract
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "IModuleManifest: manifest with no dependencies is valid")]
    public void Manifest_NoDependencies_IsValid()
    {
        var manifest = new TestModuleManifest("catalog");

        Assert.Equal(new ModuleId("catalog"), manifest.ModuleId);
        Assert.Empty(manifest.Dependencies);
        Assert.Empty(manifest.ProvidedFeatures);
        Assert.Equal(0, manifest.DatabaseSchemaVersion);
    }

    [Fact(DisplayName = "IModuleManifest: manifest with features exposes correct FeatureIds")]
    public void Manifest_WithFeatures_ExposesCorrectIds()
    {
        var feature1 = new TestFeatureDescriptor("accounting.general-ledger", "General Ledger");
        var feature2 = new TestFeatureDescriptor("accounting.accounts-payable", "Accounts Payable");

        var manifest = new TestModuleManifest(
            "accounting",
            features: [feature1, feature2]);

        Assert.Equal(2, manifest.ProvidedFeatures.Count);
        Assert.Equal(new FeatureId("accounting.general-ledger"), manifest.ProvidedFeatures[0].Id);
        Assert.Equal(new FeatureId("accounting.accounts-payable"), manifest.ProvidedFeatures[1].Id);
    }

    [Fact(DisplayName = "IModuleManifest: manifest version matches declared version")]
    public void Manifest_Version_MatchesDeclaredVersion()
    {
        var manifest = new TestModuleManifest("inventory", version: "2.5.1");
        Assert.Equal(new ModuleVersion(2, 5, 1), manifest.Version);
    }

    [Fact(DisplayName = "IModuleManifest: manifest with database schema version > 0 is valid")]
    public void Manifest_DatabaseSchemaVersion_CanBeNonZero()
    {
        var manifest = new TestModuleManifest("catalog", databaseSchemaVersion: 3);
        Assert.Equal(3, manifest.DatabaseSchemaVersion);
    }

    [Fact(DisplayName = "IModuleManifest: platform compatibility range is accessible")]
    public void Manifest_PlatformCompatibility_IsAccessible()
    {
        var manifest = new TestModuleManifest(
            "sales",
            minimumPlatformVersion: "2.0.0");

        Assert.Equal(new ModuleVersion(2, 0, 0), manifest.MinimumPlatformVersion);
        Assert.Null(manifest.MaximumPlatformVersion);
    }
}
