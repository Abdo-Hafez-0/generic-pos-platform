using Platform.Core.Modules;

namespace Platform.ModuleContract.Tests;

/// <summary>
/// Tests for ModuleDependency — construction, satisfaction, and null guards.
/// </summary>
public sealed class ModuleDependencyTests
{
    private static readonly ModuleId CatalogId = new("catalog");
    private static readonly ModuleVersion V100 = new(1, 0, 0);
    private static readonly ModuleVersion V120 = new(1, 2, 0);
    private static readonly ModuleVersion V200 = new(2, 0, 0);

    [Fact(DisplayName = "ModuleDependency: constructs with explicit version range")]
    public void Constructor_WithVersionRange_Succeeds()
    {
        var range = VersionRange.AtLeast(V120);
        var dep = new ModuleDependency(CatalogId, range);

        Assert.Equal(CatalogId, dep.RequiredModuleId);
        Assert.Equal(range, dep.VersionConstraint);
    }

    [Fact(DisplayName = "ModuleDependency: defaults to >= 0.0.0 when no range provided")]
    public void Constructor_NoRange_DefaultsToAnyVersion()
    {
        var dep = new ModuleDependency(CatalogId);

        // Any version >= 0.0.0 should be satisfied
        Assert.True(dep.IsSatisfiedBy(V100));
        Assert.True(dep.IsSatisfiedBy(V200));
    }

    [Fact(DisplayName = "ModuleDependency: null module ID throws ArgumentNullException")]
    public void Constructor_NullModuleId_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ModuleDependency(null!));
    }

    [Fact(DisplayName = "ModuleDependency: IsSatisfiedBy returns true when version meets constraint")]
    public void IsSatisfiedBy_MeetingConstraint_ReturnsTrue()
    {
        var dep = new ModuleDependency(CatalogId, VersionRange.AtLeast(V120));
        Assert.True(dep.IsSatisfiedBy(V120));
        Assert.True(dep.IsSatisfiedBy(V200));
    }

    [Fact(DisplayName = "ModuleDependency: IsSatisfiedBy returns false when version below constraint")]
    public void IsSatisfiedBy_BelowConstraint_ReturnsFalse()
    {
        var dep = new ModuleDependency(CatalogId, VersionRange.AtLeast(V120));
        Assert.False(dep.IsSatisfiedBy(V100));
    }

    [Fact(DisplayName = "ModuleDependency: IsSatisfiedBy null throws ArgumentNullException")]
    public void IsSatisfiedBy_Null_Throws()
    {
        var dep = new ModuleDependency(CatalogId);
        Assert.Throws<ArgumentNullException>(() => dep.IsSatisfiedBy(null!));
    }

    [Fact(DisplayName = "ModuleDependency: ToString includes module ID and constraint")]
    public void ToString_IncludesModuleIdAndConstraint()
    {
        var dep = new ModuleDependency(CatalogId, VersionRange.AtLeast(V120));
        var str = dep.ToString();

        Assert.Contains("catalog", str);
        Assert.Contains("1.2.0", str);
    }

    [Fact(DisplayName = "ModuleDependency: equal dependencies with same ID and range are equal")]
    public void Equality_SameIdAndRange_AreEqual()
    {
        var dep1 = new ModuleDependency(CatalogId, VersionRange.AtLeast(V120));
        var dep2 = new ModuleDependency(CatalogId, VersionRange.AtLeast(V120));

        Assert.Equal(dep1, dep2);
        Assert.Equal(dep1.GetHashCode(), dep2.GetHashCode());
    }
}
