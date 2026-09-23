using Catalog.Domain.Entities;

namespace Catalog.Tests.Domain;

/// <summary>Tests for Category and Unit entity business invariants.</summary>
public sealed class CategoryAndUnitDomainTests
{
    [Fact(DisplayName = "Category: valid creation succeeds")]
    public void Category_Create_Valid_Succeeds()
    {
        var result = Category.Create("Electronics");

        Assert.True(result.IsSuccess);
        Assert.Equal("Electronics", result.Value.Name);
        Assert.True(result.Value.IsActive);
    }

    [Fact(DisplayName = "Category: empty name fails")]
    public void Category_Create_EmptyName_ReturnsFailure()
    {
        var result = Category.Create("");

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Category.NameEmpty", result.Error.Code);
    }

    [Fact(DisplayName = "Category: name exceeding 100 chars fails")]
    public void Category_Create_NameTooLong_ReturnsFailure()
    {
        var result = Category.Create(new string('A', 101));

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Category.NameTooLong", result.Error.Code);
    }

    [Fact(DisplayName = "Category: deactivate sets IsActive to false")]
    public void Category_Deactivate_SetsIsActiveFalse()
    {
        var category = Category.Create("Electronics").Value;
        category.Deactivate();

        Assert.False(category.IsActive);
    }

    [Fact(DisplayName = "Unit: valid creation succeeds")]
    public void Unit_Create_Valid_Succeeds()
    {
        var result = Unit.Create("Piece", "pcs");

        Assert.True(result.IsSuccess);
        Assert.Equal("Piece", result.Value.Name);
        Assert.Equal("pcs", result.Value.Abbreviation);
        Assert.True(result.Value.IsActive);
    }

    [Fact(DisplayName = "Unit: empty name fails")]
    public void Unit_Create_EmptyName_ReturnsFailure()
    {
        var result = Unit.Create("", "pcs");

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Unit.NameEmpty", result.Error.Code);
    }

    [Fact(DisplayName = "Unit: empty abbreviation fails")]
    public void Unit_Create_EmptyAbbreviation_ReturnsFailure()
    {
        var result = Unit.Create("Piece", "");

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Unit.AbbreviationEmpty", result.Error.Code);
    }

    [Fact(DisplayName = "Unit: abbreviation exceeding 10 chars fails")]
    public void Unit_Create_AbbreviationTooLong_ReturnsFailure()
    {
        var result = Unit.Create("Piece", new string('x', 11));

        Assert.True(result.IsFailure);
        Assert.Equal("Catalog.Unit.AbbreviationTooLong", result.Error.Code);
    }
}
