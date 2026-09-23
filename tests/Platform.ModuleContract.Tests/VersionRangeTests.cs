using Platform.Core.Modules;

namespace Platform.ModuleContract.Tests;

/// <summary>
/// Tests for VersionRange — constraint satisfaction across all operators.
/// </summary>
public sealed class VersionRangeTests
{
    private static readonly ModuleVersion V100 = new(1, 0, 0);
    private static readonly ModuleVersion V120 = new(1, 2, 0);
    private static readonly ModuleVersion V200 = new(2, 0, 0);

    // -----------------------------------------------------------------------
    // GreaterThanOrEqual (the most common constraint)
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "VersionRange >= 1.2.0: exact match satisfies")]
    public void AtLeast_ExactMatch_Satisfied()
    {
        var range = VersionRange.AtLeast(V120);
        Assert.True(range.IsSatisfiedBy(V120));
    }

    [Fact(DisplayName = "VersionRange >= 1.2.0: higher version satisfies")]
    public void AtLeast_HigherVersion_Satisfied()
    {
        var range = VersionRange.AtLeast(V120);
        Assert.True(range.IsSatisfiedBy(V200));
    }

    [Fact(DisplayName = "VersionRange >= 1.2.0: lower version does not satisfy")]
    public void AtLeast_LowerVersion_NotSatisfied()
    {
        var range = VersionRange.AtLeast(V120);
        Assert.False(range.IsSatisfiedBy(V100));
    }

    // -----------------------------------------------------------------------
    // ExactMatch
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "VersionRange == 1.2.0: exact match satisfies")]
    public void Exactly_ExactMatch_Satisfied()
    {
        var range = VersionRange.Exactly(V120);
        Assert.True(range.IsSatisfiedBy(V120));
    }

    [Fact(DisplayName = "VersionRange == 1.2.0: different version does not satisfy")]
    public void Exactly_DifferentVersion_NotSatisfied()
    {
        var range = VersionRange.Exactly(V120);
        Assert.False(range.IsSatisfiedBy(V100));
        Assert.False(range.IsSatisfiedBy(V200));
    }

    // -----------------------------------------------------------------------
    // LessThanOrEqual
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "VersionRange <= 1.2.0: lower version satisfies")]
    public void LessThanOrEqual_LowerVersion_Satisfied()
    {
        var range = new VersionRange(V120, VersionRangeOperator.LessThanOrEqual);
        Assert.True(range.IsSatisfiedBy(V100));
        Assert.True(range.IsSatisfiedBy(V120));
    }

    [Fact(DisplayName = "VersionRange <= 1.2.0: higher version does not satisfy")]
    public void LessThanOrEqual_HigherVersion_NotSatisfied()
    {
        var range = new VersionRange(V120, VersionRangeOperator.LessThanOrEqual);
        Assert.False(range.IsSatisfiedBy(V200));
    }

    // -----------------------------------------------------------------------
    // Strict inequalities
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "VersionRange > 1.2.0: strictly higher version satisfies")]
    public void GreaterThan_StrictlyHigher_Satisfied()
    {
        var range = new VersionRange(V120, VersionRangeOperator.GreaterThan);
        Assert.True(range.IsSatisfiedBy(V200));
    }

    [Fact(DisplayName = "VersionRange > 1.2.0: exact match does not satisfy")]
    public void GreaterThan_ExactMatch_NotSatisfied()
    {
        var range = new VersionRange(V120, VersionRangeOperator.GreaterThan);
        Assert.False(range.IsSatisfiedBy(V120));
    }

    [Fact(DisplayName = "VersionRange < 1.2.0: strictly lower version satisfies")]
    public void LessThan_StrictlyLower_Satisfied()
    {
        var range = new VersionRange(V120, VersionRangeOperator.LessThan);
        Assert.True(range.IsSatisfiedBy(V100));
    }

    [Fact(DisplayName = "VersionRange < 1.2.0: exact match does not satisfy")]
    public void LessThan_ExactMatch_NotSatisfied()
    {
        var range = new VersionRange(V120, VersionRangeOperator.LessThan);
        Assert.False(range.IsSatisfiedBy(V120));
    }

    // -----------------------------------------------------------------------
    // Null guard
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "VersionRange: null bound throws ArgumentNullException")]
    public void Constructor_NullBound_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new VersionRange(null!));
    }

    [Fact(DisplayName = "VersionRange.IsSatisfiedBy: null throws ArgumentNullException")]
    public void IsSatisfiedBy_NullCandidate_Throws()
    {
        var range = VersionRange.AtLeast(V100);
        Assert.Throws<ArgumentNullException>(() => range.IsSatisfiedBy(null!));
    }

    // -----------------------------------------------------------------------
    // Equality
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "VersionRange: same bound and operator are equal")]
    public void Equality_SameBoundAndOperator_Equal()
    {
        var a = VersionRange.AtLeast(V120);
        var b = VersionRange.AtLeast(V120);
        Assert.Equal(a, b);
    }

    [Fact(DisplayName = "VersionRange: different operator are not equal")]
    public void Equality_DifferentOperator_NotEqual()
    {
        var a = VersionRange.AtLeast(V120);
        var b = VersionRange.Exactly(V120);
        Assert.NotEqual(a, b);
    }

    // -----------------------------------------------------------------------
    // ToString
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "VersionRange.ToString produces readable constraint string")]
    public void ToString_ProducesReadableString()
    {
        var range = VersionRange.AtLeast(V120);
        Assert.Equal(">= 1.2.0", range.ToString());
    }
}
