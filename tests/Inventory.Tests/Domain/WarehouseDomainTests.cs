using Inventory.Domain.Entities;
using Inventory.Domain.ValueObjects;
using Inventory.Domain.Events;

namespace Inventory.Tests.Domain;

/// <summary>
/// Unit tests for Warehouse aggregate invariants and behaviour.
/// Tests run entirely in-process — no database required.
/// </summary>
public sealed class WarehouseDomainTests
{
    // -----------------------------------------------------------------------
    // Warehouse.Create
    // -----------------------------------------------------------------------

    [Fact]
    public void Create_ValidNameAndCode_ReturnsSuccess()
    {
        var result = Warehouse.Create("Main Warehouse", "MAIN");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("Main Warehouse", result.Value.Name);
        Assert.Equal("MAIN", result.Value.Code);
        Assert.True(result.Value.IsActive);
        Assert.NotEqual(WarehouseId.Empty, result.Value.Id);
    }

    [Fact]
    public void Create_CodesIsUppercased()
    {
        var result = Warehouse.Create("Store", "store-1");

        Assert.True(result.IsSuccess);
        Assert.Equal("STORE-1", result.Value.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_EmptyName_ReturnsFailure(string name)
    {
        var result = Warehouse.Create(name, "WH1");

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.Warehouse.NameEmpty", result.Error.Code);
    }

    [Fact]
    public void Create_NameTooLong_ReturnsFailure()
    {
        var result = Warehouse.Create(new string('A', 101), "WH1");

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.Warehouse.NameTooLong", result.Error.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_EmptyCode_ReturnsFailure(string code)
    {
        var result = Warehouse.Create("Warehouse", code);

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.Warehouse.CodeEmpty", result.Error.Code);
    }

    [Fact]
    public void Create_RaisesWarehouseCreatedEvent()
    {
        var result = Warehouse.Create("Depot", "DEPOT");

        Assert.True(result.IsSuccess);
        var events = result.Value.DomainEvents;
        Assert.Single(events);
        Assert.IsType<WarehouseCreatedEvent>(events[0]);
        var ev = (WarehouseCreatedEvent)events[0];
        Assert.Equal("DEPOT", ev.Code);
    }

    // -----------------------------------------------------------------------
    // Warehouse.Deactivate / Activate
    // -----------------------------------------------------------------------

    [Fact]
    public void Deactivate_ActiveWarehouse_Succeeds()
    {
        var warehouse = Warehouse.Create("WH", "WH").Value;
        var result = warehouse.Deactivate();

        Assert.True(result.IsSuccess);
        Assert.False(warehouse.IsActive);
    }

    [Fact]
    public void Deactivate_AlreadyInactive_ReturnsConflict()
    {
        var warehouse = Warehouse.Create("WH", "WH").Value;
        warehouse.Deactivate();
        var result = warehouse.Deactivate();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.Warehouse.AlreadyInactive", result.Error.Code);
    }

    [Fact]
    public void Activate_InactiveWarehouse_Succeeds()
    {
        var warehouse = Warehouse.Create("WH", "WH").Value;
        warehouse.Deactivate();
        var result = warehouse.Activate();

        Assert.True(result.IsSuccess);
        Assert.True(warehouse.IsActive);
    }

    [Fact]
    public void Activate_AlreadyActive_ReturnsConflict()
    {
        var warehouse = Warehouse.Create("WH", "WH").Value;
        var result = warehouse.Activate();

        Assert.True(result.IsFailure);
        Assert.Equal("Inventory.Warehouse.AlreadyActive", result.Error.Code);
    }

    // -----------------------------------------------------------------------
    // ClearDomainEvents
    // -----------------------------------------------------------------------

    [Fact]
    public void ClearDomainEvents_RemovesAllEvents()
    {
        var warehouse = Warehouse.Create("WH", "WH").Value;
        Assert.NotEmpty(warehouse.DomainEvents);

        warehouse.ClearDomainEvents();
        Assert.Empty(warehouse.DomainEvents);
    }
}
