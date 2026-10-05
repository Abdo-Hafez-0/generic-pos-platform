using Suppliers.Domain.Enums;
using Suppliers.Domain.ValueObjects;
using Platform.Core.Results;

namespace Suppliers.Domain.Entities;

/// <summary>A postal address of a supplier (owned by the Supplier aggregate).</summary>
public sealed class SupplierAddress
{
    private SupplierAddress() { }

    public SupplierAddressId Id { get; private set; }
    public SupplierId SupplierId { get; private set; }
    public AddressType Type { get; private set; }
    public string Line1 { get; private set; } = string.Empty;
    public string? Line2 { get; private set; }
    public string City { get; private set; } = string.Empty;
    public string? Region { get; private set; }
    public string? PostalCode { get; private set; }
    public string Country { get; private set; } = string.Empty;

    internal static Result<SupplierAddress> Create(
        SupplierId supplierId, AddressType type, string line1, string? line2, string city, string? region, string? postalCode, string country)
    {
        if (!Enum.IsDefined(type))
            return Result.Failure<SupplierAddress>(Error.Validation("Suppliers.Address.TypeInvalid", "The address type is not valid."));
        if (string.IsNullOrWhiteSpace(line1))
            return Result.Failure<SupplierAddress>(Error.Validation("Suppliers.Address.Line1Required", "Address line 1 is required."));
        if (string.IsNullOrWhiteSpace(city))
            return Result.Failure<SupplierAddress>(Error.Validation("Suppliers.Address.CityRequired", "The city is required."));
        if (string.IsNullOrWhiteSpace(country))
            return Result.Failure<SupplierAddress>(Error.Validation("Suppliers.Address.CountryRequired", "The country is required."));
        if (line1.Trim().Length > 200 || (line2?.Trim().Length ?? 0) > 200 || city.Trim().Length > 100 || country.Trim().Length > 100)
            return Result.Failure<SupplierAddress>(Error.Validation("Suppliers.Address.TooLong", "An address field is too long."));

        return Result.Success(new SupplierAddress
        {
            Id = SupplierAddressId.New(),
            SupplierId = supplierId,
            Type = type,
            Line1 = line1.Trim(),
            Line2 = Supplier.Clean(line2),
            City = city.Trim(),
            Region = Supplier.Clean(region),
            PostalCode = Supplier.Clean(postalCode),
            Country = country.Trim()
        });
    }
}

/// <summary>A named contact person at a supplier (owned by the Supplier aggregate).</summary>
public sealed class SupplierContact
{
    private SupplierContact() { }

    public SupplierContactId Id { get; private set; }
    public SupplierId SupplierId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? Role { get; private set; }

    internal static Result<SupplierContact> Create(SupplierId supplierId, string name, string? email, string? phone, string? role)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<SupplierContact>(Error.Validation("Suppliers.Contact.NameRequired", "The contact name is required."));
        if (name.Trim().Length > 200)
            return Result.Failure<SupplierContact>(Error.Validation("Suppliers.Contact.NameTooLong", "The contact name cannot exceed 200 characters."));
        if (!string.IsNullOrWhiteSpace(email) && !Supplier.LooksLikeEmail(email.Trim()))
            return Result.Failure<SupplierContact>(Error.Validation("Suppliers.Contact.EmailInvalid", "The contact email address is not valid."));

        return Result.Success(new SupplierContact
        {
            Id = SupplierContactId.New(),
            SupplierId = supplierId,
            Name = name.Trim(),
            Email = Supplier.Clean(email),
            Phone = Supplier.Clean(phone),
            Role = Supplier.Clean(role)
        });
    }
}
