using Suppliers.Domain.Entities;
using Suppliers.Domain.Enums;
using Suppliers.Domain.Events;
using Suppliers.Domain.ValueObjects;

namespace Suppliers.Tests.Domain;

public sealed class SupplierDomainTests
{
    private static Supplier New(string code = "cust-1", string name = "Acme Ltd", string? email = "buyer@acme.test", string? phone = "555-0100")
    {
        var r = Supplier.Create(code, name, email, phone, "vip");
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        return r.Value;
    }

    [Fact]
    public void Create_NormalisesCode_StartsActive_AndRaisesEvent()
    {
        var c = New("  cust-1 ", "  Acme Ltd ");

        Assert.Equal("CUST-1", c.Code);
        Assert.Equal("Acme Ltd", c.Name);
        Assert.Equal(SupplierStatus.Active, c.Status);
        Assert.NotEqual(SupplierId.Empty, c.Id);
        var evt = Assert.IsType<SupplierCreatedEvent>(Assert.Single(c.DomainEvents));
        Assert.Equal("CUST-1", evt.Code);
    }

    [Theory]
    [InlineData("", "Name", "Suppliers.Supplier.CodeRequired")]
    [InlineData("  ", "Name", "Suppliers.Supplier.CodeRequired")]
    [InlineData("C", "", "Suppliers.Supplier.NameRequired")]
    [InlineData("C", "   ", "Suppliers.Supplier.NameRequired")]
    public void Create_RequiresCodeAndName(string code, string name, string expected)
    {
        var r = Supplier.Create(code, name);

        Assert.True(r.IsFailure);
        Assert.Equal(expected, r.Error.Code);
    }

    [Fact]
    public void Create_RejectsTooLongValuesAndBadEmail()
    {
        Assert.Equal("Suppliers.Supplier.CodeTooLong", Supplier.Create(new string('x', 31), "N").Error.Code);
        Assert.Equal("Suppliers.Supplier.NameTooLong", Supplier.Create("C", new string('x', 201)).Error.Code);
        Assert.Equal("Suppliers.Supplier.EmailInvalid", Supplier.Create("C", "N", "not-an-email").Error.Code);
        Assert.Equal("Suppliers.Supplier.EmailInvalid", Supplier.Create("C", "N", "a b@c.d").Error.Code);
        Assert.Equal("Suppliers.Supplier.PhoneTooLong", Supplier.Create("C", "N", null, new string('1', 41)).Error.Code);
        Assert.Equal("Suppliers.Supplier.NotesTooLong", Supplier.Create("C", "N", null, null, new string('n', 1001)).Error.Code);
    }

    [Fact]
    public void Create_BlankOptionalFields_BecomeNull()
    {
        var c = Supplier.Create("C", "N", "  ", "", " ").Value;

        Assert.Null(c.Email);
        Assert.Null(c.Phone);
        Assert.Null(c.Notes);
    }

    [Fact]
    public void Update_ChangesEditableFields_ButNeverTheCode()
    {
        var c = New();

        var r = c.Update("New Name", "x@y.z", null, "n");

        Assert.True(r.IsSuccess);
        Assert.Equal("New Name", c.Name);
        Assert.Equal("x@y.z", c.Email);
        Assert.Null(c.Phone);
        Assert.Equal("CUST-1", c.Code);
    }

    [Fact]
    public void Update_InvalidValues_Fail_AndChangeNothing()
    {
        var c = New();

        var r = c.Update("", "x@y.z", null, null);

        Assert.Equal("Suppliers.Supplier.NameRequired", r.Error.Code);
        Assert.Equal("Acme Ltd", c.Name);
    }

    [Fact]
    public void Deactivate_Then_Reactivate_FollowTheStateRules()
    {
        var c = New();
        c.ClearDomainEvents();

        Assert.True(c.Deactivate().IsSuccess);
        Assert.Equal(SupplierStatus.Inactive, c.Status);
        Assert.IsType<SupplierDeactivatedEvent>(Assert.Single(c.DomainEvents));
        Assert.Equal("Suppliers.Supplier.AlreadyInactive", c.Deactivate().Error.Code);

        Assert.True(c.Reactivate().IsSuccess);
        Assert.Equal(SupplierStatus.Active, c.Status);
        Assert.Equal("Suppliers.Supplier.AlreadyActive", c.Reactivate().Error.Code);
    }

    [Fact]
    public void InactiveSupplier_CannotBeModified()
    {
        var c = New();
        c.Deactivate();

        Assert.Equal("Suppliers.Supplier.Inactive", c.Update("X", null, null, null).Error.Code);
        Assert.Equal("Suppliers.Supplier.Inactive", c.AddAddress(AddressType.Billing, "1 St", null, "City", null, null, "Country").Error.Code);
        Assert.Equal("Suppliers.Supplier.Inactive", c.AddContact("Bob", null, null, null).Error.Code);
        Assert.Equal("Suppliers.Supplier.Inactive", c.RemoveAddress(SupplierAddressId.New()).Error.Code);
        Assert.Equal("Suppliers.Supplier.Inactive", c.RemoveContact(SupplierContactId.New()).Error.Code);
    }

    [Fact]
    public void Addresses_CanBeAddedAndRemoved_WithValidation()
    {
        var c = New();

        var added = c.AddAddress(AddressType.Shipping, " 1 Main St ", null, "Springfield", "IL", "62701", "USA");

        Assert.True(added.IsSuccess);
        var address = Assert.Single(c.Addresses);
        Assert.Equal("1 Main St", address.Line1);
        Assert.Equal(c.Id, address.SupplierId);

        Assert.Equal("Suppliers.Address.Line1Required", c.AddAddress(AddressType.Billing, "", null, "C", null, null, "X").Error.Code);
        Assert.Equal("Suppliers.Address.CityRequired", c.AddAddress(AddressType.Billing, "1", null, " ", null, null, "X").Error.Code);
        Assert.Equal("Suppliers.Address.CountryRequired", c.AddAddress(AddressType.Billing, "1", null, "C", null, null, "").Error.Code);
        Assert.Equal("Suppliers.Address.TypeInvalid", c.AddAddress((AddressType)99, "1", null, "C", null, null, "X").Error.Code);
        Assert.Single(c.Addresses);

        Assert.True(c.RemoveAddress(address.Id).IsSuccess);
        Assert.Empty(c.Addresses);
        Assert.Equal("Suppliers.Supplier.AddressNotFound", c.RemoveAddress(address.Id).Error.Code);
    }

    [Fact]
    public void Contacts_CanBeAddedAndRemoved_WithValidation()
    {
        var c = New();

        var added = c.AddContact("  Jane ", "jane@acme.test", "555", "Buyer");

        Assert.True(added.IsSuccess);
        var contact = Assert.Single(c.Contacts);
        Assert.Equal("Jane", contact.Name);
        Assert.Equal("Buyer", contact.Role);

        Assert.Equal("Suppliers.Contact.NameRequired", c.AddContact(" ", null, null, null).Error.Code);
        Assert.Equal("Suppliers.Contact.EmailInvalid", c.AddContact("X", "bad", null, null).Error.Code);
        Assert.Equal("Suppliers.Contact.NameTooLong", c.AddContact(new string('x', 201), null, null, null).Error.Code);

        Assert.True(c.RemoveContact(contact.Id).IsSuccess);
        Assert.Equal("Suppliers.Supplier.ContactNotFound", c.RemoveContact(contact.Id).Error.Code);
    }

    [Fact]
    public void Ids_AreUnique_AndEmptyIsEmpty()
    {
        Assert.NotEqual(SupplierId.New(), SupplierId.New());
        Assert.NotEqual(SupplierAddressId.New(), SupplierAddressId.New());
        Assert.NotEqual(SupplierContactId.New(), SupplierContactId.New());
        Assert.Equal(Guid.Empty, SupplierId.Empty.Value);
    }
}
