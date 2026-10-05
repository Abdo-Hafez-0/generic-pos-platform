using Customers.Domain.Enums;
using Customers.Domain.ValueObjects;
using Platform.Core.Results;

namespace Customers.Domain.Entities;

/// <summary>A postal address of a customer (owned by the Customer aggregate).</summary>
public sealed class CustomerAddress
{
    private CustomerAddress() { }

    public CustomerAddressId Id { get; private set; }
    public CustomerId CustomerId { get; private set; }
    public AddressType Type { get; private set; }
    public string Line1 { get; private set; } = string.Empty;
    public string? Line2 { get; private set; }
    public string City { get; private set; } = string.Empty;
    public string? Region { get; private set; }
    public string? PostalCode { get; private set; }
    public string Country { get; private set; } = string.Empty;

    internal static Result<CustomerAddress> Create(
        CustomerId customerId, AddressType type, string line1, string? line2, string city, string? region, string? postalCode, string country)
    {
        if (!Enum.IsDefined(type))
            return Result.Failure<CustomerAddress>(Error.Validation("Customers.Address.TypeInvalid", "The address type is not valid."));
        if (string.IsNullOrWhiteSpace(line1))
            return Result.Failure<CustomerAddress>(Error.Validation("Customers.Address.Line1Required", "Address line 1 is required."));
        if (string.IsNullOrWhiteSpace(city))
            return Result.Failure<CustomerAddress>(Error.Validation("Customers.Address.CityRequired", "The city is required."));
        if (string.IsNullOrWhiteSpace(country))
            return Result.Failure<CustomerAddress>(Error.Validation("Customers.Address.CountryRequired", "The country is required."));
        if (line1.Trim().Length > 200 || (line2?.Trim().Length ?? 0) > 200 || city.Trim().Length > 100 || country.Trim().Length > 100)
            return Result.Failure<CustomerAddress>(Error.Validation("Customers.Address.TooLong", "An address field is too long."));

        return Result.Success(new CustomerAddress
        {
            Id = CustomerAddressId.New(),
            CustomerId = customerId,
            Type = type,
            Line1 = line1.Trim(),
            Line2 = Customer.Clean(line2),
            City = city.Trim(),
            Region = Customer.Clean(region),
            PostalCode = Customer.Clean(postalCode),
            Country = country.Trim()
        });
    }
}

/// <summary>A named contact person at a customer (owned by the Customer aggregate).</summary>
public sealed class CustomerContact
{
    private CustomerContact() { }

    public CustomerContactId Id { get; private set; }
    public CustomerId CustomerId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? Role { get; private set; }

    internal static Result<CustomerContact> Create(CustomerId customerId, string name, string? email, string? phone, string? role)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Result.Failure<CustomerContact>(Error.Validation("Customers.Contact.NameRequired", "The contact name is required."));
        if (name.Trim().Length > 200)
            return Result.Failure<CustomerContact>(Error.Validation("Customers.Contact.NameTooLong", "The contact name cannot exceed 200 characters."));
        if (!string.IsNullOrWhiteSpace(email) && !Customer.LooksLikeEmail(email.Trim()))
            return Result.Failure<CustomerContact>(Error.Validation("Customers.Contact.EmailInvalid", "The contact email address is not valid."));

        return Result.Success(new CustomerContact
        {
            Id = CustomerContactId.New(),
            CustomerId = customerId,
            Name = name.Trim(),
            Email = Customer.Clean(email),
            Phone = Customer.Clean(phone),
            Role = Customer.Clean(role)
        });
    }
}
