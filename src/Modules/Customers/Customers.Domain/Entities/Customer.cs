using Customers.Domain.Enums;
using Customers.Domain.Events;
using Customers.Domain.ValueObjects;
using Platform.Core.Results;

namespace Customers.Domain.Entities;

/// <summary>
/// A customer (aggregate root). Owns its addresses and contacts.
///
/// The model deliberately carries only identity and contact data: customer-specific pricing, credit, loyalty and
/// sales history belong to OTHER modules that reference a customer by CustomerId (through Customers.Contracts).
/// </summary>
public sealed class Customer
{
    private readonly List<CustomerAddress> _addresses = [];
    private readonly List<CustomerContact> _contacts = [];
    private readonly List<object> _domainEvents = [];

    private Customer() { }

    public CustomerId Id { get; private set; }

    /// <summary>Business-visible unique code (upper-case), e.g. "CUST-0001".</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? Notes { get; private set; }
    public CustomerStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public IReadOnlyList<CustomerAddress> Addresses => _addresses.AsReadOnly();
    public IReadOnlyList<CustomerContact> Contacts => _contacts.AsReadOnly();
    public IReadOnlyList<object> DomainEvents => _domainEvents.AsReadOnly();
    public void ClearDomainEvents() => _domainEvents.Clear();

    public static Result<Customer> Create(string code, string name, string? email = null, string? phone = null, string? notes = null)
    {
        var validation = Validate(code, name, email, phone, notes);
        if (validation is not null) return Result.Failure<Customer>(validation);

        var now = DateTime.UtcNow;
        var customer = new Customer
        {
            Id = CustomerId.New(),
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            Email = Clean(email),
            Phone = Clean(phone),
            Notes = Clean(notes),
            Status = CustomerStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };

        customer._domainEvents.Add(new CustomerCreatedEvent(customer.Id, customer.Code, now));
        return Result.Success(customer);
    }

    /// <summary>Updates the editable fields. The code is the customer's stable business identity and cannot change.</summary>
    public Result Update(string name, string? email, string? phone, string? notes)
    {
        if (Status != CustomerStatus.Active) return Result.Failure(InactiveError());

        var validation = Validate(Code, name, email, phone, notes);
        if (validation is not null) return Result.Failure(validation);

        Name = name.Trim();
        Email = Clean(email);
        Phone = Clean(phone);
        Notes = Clean(notes);
        Touch();
        return Result.Success();
    }

    public Result Deactivate()
    {
        if (Status == CustomerStatus.Inactive)
            return Result.Failure(Error.Conflict("Customers.Customer.AlreadyInactive", "The customer is already inactive."));

        Status = CustomerStatus.Inactive;
        Touch();
        _domainEvents.Add(new CustomerDeactivatedEvent(Id, UpdatedAt));
        return Result.Success();
    }

    public Result Reactivate()
    {
        if (Status == CustomerStatus.Active)
            return Result.Failure(Error.Conflict("Customers.Customer.AlreadyActive", "The customer is already active."));

        Status = CustomerStatus.Active;
        Touch();
        return Result.Success();
    }

    public Result<CustomerAddress> AddAddress(
        AddressType type, string line1, string? line2, string city, string? region, string? postalCode, string country)
    {
        if (Status != CustomerStatus.Active) return Result.Failure<CustomerAddress>(InactiveError());

        var created = CustomerAddress.Create(Id, type, line1, line2, city, region, postalCode, country);
        if (created.IsFailure) return created;

        _addresses.Add(created.Value);
        Touch();
        return created;
    }

    public Result RemoveAddress(CustomerAddressId addressId)
    {
        if (Status != CustomerStatus.Active) return Result.Failure(InactiveError());

        var address = _addresses.FirstOrDefault(a => a.Id == addressId);
        if (address is null)
            return Result.Failure(Error.NotFound("Customers.Customer.AddressNotFound", "The address was not found on this customer."));

        _addresses.Remove(address);
        Touch();
        return Result.Success();
    }

    public Result<CustomerContact> AddContact(string name, string? email, string? phone, string? role)
    {
        if (Status != CustomerStatus.Active) return Result.Failure<CustomerContact>(InactiveError());

        var created = CustomerContact.Create(Id, name, email, phone, role);
        if (created.IsFailure) return created;

        _contacts.Add(created.Value);
        Touch();
        return created;
    }

    public Result RemoveContact(CustomerContactId contactId)
    {
        if (Status != CustomerStatus.Active) return Result.Failure(InactiveError());

        var contact = _contacts.FirstOrDefault(c => c.Id == contactId);
        if (contact is null)
            return Result.Failure(Error.NotFound("Customers.Customer.ContactNotFound", "The contact was not found on this customer."));

        _contacts.Remove(contact);
        Touch();
        return Result.Success();
    }

    private void Touch() => UpdatedAt = DateTime.UtcNow;

    private static Error InactiveError()
        => Error.Conflict("Customers.Customer.Inactive", "The customer is inactive. Reactivate it before changing it.");

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static bool LooksLikeEmail(string email)
        => email.Length <= 254 && email.Contains('@') && !email.StartsWith('@') && !email.EndsWith('@') && !email.Contains(' ');

    private static Error? Validate(string code, string name, string? email, string? phone, string? notes)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Error.Validation("Customers.Customer.CodeRequired", "A customer code is required.");
        if (code.Trim().Length > 30)
            return Error.Validation("Customers.Customer.CodeTooLong", "The customer code cannot exceed 30 characters.");
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("Customers.Customer.NameRequired", "A customer name is required.");
        if (name.Trim().Length > 200)
            return Error.Validation("Customers.Customer.NameTooLong", "The customer name cannot exceed 200 characters.");
        if (!string.IsNullOrWhiteSpace(email) && !LooksLikeEmail(email.Trim()))
            return Error.Validation("Customers.Customer.EmailInvalid", "The email address is not valid.");
        if (phone is not null && phone.Trim().Length > 40)
            return Error.Validation("Customers.Customer.PhoneTooLong", "The phone number cannot exceed 40 characters.");
        if (notes is not null && notes.Length > 1000)
            return Error.Validation("Customers.Customer.NotesTooLong", "Notes cannot exceed 1000 characters.");
        return null;
    }
}
