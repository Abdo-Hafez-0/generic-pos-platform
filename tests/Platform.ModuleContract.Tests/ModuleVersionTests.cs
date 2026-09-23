using Platform.Core.Modules;

namespace Platform.ModuleContract.Tests;

/// <summary>
/// Tests for ModuleVersion — parsing, comparison, equality, and edge cases.
/// </summary>
public sealed class ModuleVersionTests
{
    // -----------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "ModuleVersion: constructs with valid Major.Minor.Patch")]
    public void Constructor_ValidComponents_Succeeds()
    {
        var v = new ModuleVersion(1, 2, 3);
        Assert.Equal(1, v.Major);
        Assert.Equal(2, v.Minor);
        Assert.Equal(3, v.Patch);
    }

    [Fact(DisplayName = "ModuleVersion: Patch defaults to 0 when omitted")]
    public void Constructor_OmittedPatch_DefaultsToZero()
    {
        var v = new ModuleVersion(2, 5);
        Assert.Equal(0, v.Patch);
    }

    [Theory(DisplayName = "ModuleVersion: negative components are rejected")]
    [InlineData(-1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    public void Constructor_NegativeComponent_Throws(int major, int minor, int patch)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModuleVersion(major, minor, patch));
    }

    // -----------------------------------------------------------------------
    // Parsing
    // -----------------------------------------------------------------------

    [Theory(DisplayName = "ModuleVersion.Parse: valid strings succeed")]
    [InlineData("1.2.3",   1, 2, 3)]
    [InlineData("0.0.0",   0, 0, 0)]
    [InlineData("10.0.11", 10, 0, 11)]
    [InlineData("2.0",     2, 0, 0)]
    [InlineData("1",       1, 0, 0)]
    public void Parse_ValidStrings_ReturnsExpected(string input, int major, int minor, int patch)
    {
        var v = ModuleVersion.Parse(input);
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
    }

    [Theory(DisplayName = "ModuleVersion.Parse: invalid strings throw FormatException")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("abc")]
    [InlineData("1.2.3.4")]
    [InlineData("-1.0.0")]
    [InlineData("1.-2.0")]
    public void Parse_InvalidStrings_ThrowsFormatException(string input)
    {
        Assert.Throws<FormatException>(() => ModuleVersion.Parse(input));
    }

    [Fact(DisplayName = "ModuleVersion.TryParse: null returns false")]
    public void TryParse_Null_ReturnsFalse()
    {
        Assert.False(ModuleVersion.TryParse(null, out var result));
        Assert.Null(result);
    }

    // -----------------------------------------------------------------------
    // Equality
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "ModuleVersion: equal versions are equal")]
    public void Equality_SameComponents_AreEqual()
    {
        var a = new ModuleVersion(1, 2, 3);
        var b = new ModuleVersion(1, 2, 3);
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
    }

    [Fact(DisplayName = "ModuleVersion: different versions are not equal")]
    public void Equality_DifferentComponents_AreNotEqual()
    {
        var a = new ModuleVersion(1, 2, 3);
        var b = new ModuleVersion(1, 2, 4);
        Assert.NotEqual(a, b);
        Assert.True(a != b);
    }

    [Fact(DisplayName = "ModuleVersion: equal versions have same hash code")]
    public void HashCode_EqualVersions_SameHash()
    {
        var a = new ModuleVersion(1, 2, 3);
        var b = new ModuleVersion(1, 2, 3);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // -----------------------------------------------------------------------
    // Comparison
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "ModuleVersion: major version dominates comparison")]
    public void Comparison_MajorDominates()
    {
        var a = new ModuleVersion(2, 0, 0);
        var b = new ModuleVersion(1, 9, 9);
        Assert.True(a > b);
    }

    [Fact(DisplayName = "ModuleVersion: minor version compared when major equal")]
    public void Comparison_MinorUsedWhenMajorEqual()
    {
        var a = new ModuleVersion(1, 5, 0);
        var b = new ModuleVersion(1, 3, 9);
        Assert.True(a > b);
    }

    [Fact(DisplayName = "ModuleVersion: patch version compared when major and minor equal")]
    public void Comparison_PatchUsedWhenMajorMinorEqual()
    {
        var a = new ModuleVersion(1, 2, 5);
        var b = new ModuleVersion(1, 2, 3);
        Assert.True(a > b);
        Assert.True(b < a);
    }

    [Fact(DisplayName = "ModuleVersion: operators work correctly")]
    public void Operators_AllWork()
    {
        var older = new ModuleVersion(1, 0, 0);
        var newer = new ModuleVersion(2, 0, 0);
        var olderCopy = new ModuleVersion(1, 0, 0); // same value, separate instance

        Assert.True(older < newer);
        Assert.True(older <= newer);
        Assert.True(newer > older);
        Assert.True(newer >= older);
        Assert.True(older <= olderCopy);  // equal versions satisfy <=
        Assert.True(older >= olderCopy);  // equal versions satisfy >=
    }

    // -----------------------------------------------------------------------
    // ToString
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "ModuleVersion.ToString produces 'Major.Minor.Patch' format")]
    public void ToString_FormatsCorrectly()
    {
        var v = new ModuleVersion(3, 1, 4);
        Assert.Equal("3.1.4", v.ToString());
    }
}
