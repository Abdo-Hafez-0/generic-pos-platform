using Suppliers.Domain.Enums;
using Suppliers.Domain.Events;
using Suppliers.Domain.ValueObjects;
using Platform.Core.Results;

namespace Suppliers.Domain.Entities;

/// <summary>
/// A supplier (aggregate root). Owns its addresses and contacts.
///
/// The model deliberately carries only identity and contact data: supplier-specific pricing, credit, loyalty and
/// sales history belong to OTHER modules that reference a supplier by SupplierId (through Suppliers.Contracts).
/// </summary>
public sealed class Supplier
{
    private readonly List<SupplierAddress> _addresses = [];
    private readonly List<SupplierContact> _contacts = [];
    private readonly List<object> _domainEvents = [];

    private Supplier() { }

    public SupplierId Id { get; private set; }

    /// <summary>Business-visible unique code (upper-case), e.g. "SUPT-0001".</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? Notes { get; private set; }
    public SupplierStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    public IReadOnlyList<SupplierAddress> Addresses => _addresses.AsReadOnly();
    public IReadOnlyList<SupplierContact> Contacts => _contacts.AsReadOnly();
    public IReadOnlyList<object> DomainEvents => _domainEvents.AsReadOnly();
    public void ClearDomainEvents() => _domainEvents.Clear();

    public static Result<Supplier> Create(string code, string name, string? email = null, string? phone = null, string? notes = null)
    {
        var validation = Validate(code, name, email, phone, notes);
        if (validation is not null) return Result.Failure<Supplier>(validation);

        var now = DateTime.UtcNow;
        var supplier = new Supplier
        {
            Id = SupplierId.New(),
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            Email = Clean(email),
            Phone = Clean(phone),
            Notes = Clean(notes),
            Status = SupplierStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };

        supplier._domainEvents.Add(new SupplierCreatedEvent(supplier.Id, supplier.Code, now));
        return Result.Success(supplier);
    }

    /// <summary>Updates the editable fields. The code is the supplier's stable business identity and cannot change.</summary>
    public Result Update(string name, string? email, string? phone, string? notes)
    {
        if (Status != SupplierStatus.Active) return Result.Failure(InactiveError());

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
        if (Status == SupplierStatus.Inactive)
            return Result.Failure(Error.Conflict("Suppliers.Supplier.AlreadyInactive", "The supplier is already inactive."));

        Status = SupplierStatus.Inactive;
        Touch();
        _domainEvents.Add(new SupplierDeactivatedEvent(Id, UpdatedAt));
        return Result.Success();
    }

    public Result Reactivate()
    {
        if (Status == SupplierStatus.Active)
            return Result.Failure(Error.Conflict("Suppliers.Supplier.AlreadyActive", "The supplier is already active."));

        Status = SupplierStatus.Active;
        Touch();
        return Result.Success();
    }

    public Result<SupplierAddress> AddAddress(
        AddressType type, string line1, string? line2, string city, string? region, string? postalCode, string country)
    {
        if (Status != SupplierStatus.Active) return Result.Failure<SupplierAddress>(InactiveError());

        var created = SupplierAddress.Create(Id, type, line1, line2, city, region, postalCode, country);
        if (created.IsFailure) return created;

        _addresses.Add(created.Value);
        Touch();
        return created;
    }

    public Result RemoveAddress(SupplierAddressId addressId)
    {
        if (Status != SupplierStatus.Active) return Result.Failure(InactiveError());

        var address = _addresses.FirstOrDefault(a => a.Id == addressId);
        if (address is null)
            return Result.Failure(Error.NotFound("Suppliers.Supplier.AddressNotFound", "The address was not found on this supplier."));

        _addresses.Remove(address);
        Touch();
        return Result.Success();
    }

    public Result<SupplierContact> AddContact(string name, string? email, string? phone, string? role)
    {
        if (Status != SupplierStatus.Active) return Result.Failure<SupplierContact>(InactiveError());

        var created = SupplierContact.Create(Id, name, email, phone, role);
        if (created.IsFailure) return created;

        _contacts.Add(created.Value);
        Touch();
        return created;
    }

    public Result RemoveContact(SupplierContactId contactId)
    {
        if (Status != SupplierStatus.Active) return Result.Failure(InactiveError());

        var contact = _contacts.FirstOrDefault(c => c.Id == contactId);
        if (contact is null)
            return Result.Failure(Error.NotFound("Suppliers.Supplier.ContactNotFound", "The contact was not found on this supplier."));

        _contacts.Remove(contact);
        Touch();
        return Result.Success();
    }

    private void Touch() => UpdatedAt = DateTime.UtcNow;

    private static Error InactiveError()
        => Error.Conflict("Suppliers.Supplier.Inactive", "The supplier is inactive. Reactivate it before changing it.");

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static bool LooksLikeEmail(string email)
        => email.Length <= 254 && email.Contains('@') && !email.StartsWith('@') && !email.EndsWith('@') && !email.Contains(' ');

    private static Error? Validate(string code, string name, string? email, string? phone, string? notes)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Error.Validation("Suppliers.Supplier.CodeRequired", "A supplier code is required.");
        if (code.Trim().Length > 30)
            return Error.Validation("Suppliers.Supplier.CodeTooLong", "The supplier code cannot exceed 30 characters.");
        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("Suppliers.Supplier.NameRequired", "A supplier name is required.");
        if (name.Trim().Length > 200)
            return Error.Validation("Suppliers.Supplier.NameTooLong", "The supplier name cannot exceed 200 characters.");
        if (!string.IsNullOrWhiteSpace(email) && !LooksLikeEmail(email.Trim()))
            return Error.Validation("Suppliers.Supplier.EmailInvalid", "The email address is not valid.");
        if (phone is not null && phone.Trim().Length > 40)
            return Error.Validation("Suppliers.Supplier.PhoneTooLong", "The phone number cannot exceed 40 characters.");
        if (notes is not null && notes.Length > 1000)
            return Error.Validation("Suppliers.Supplier.NotesTooLong", "Notes cannot exceed 1000 characters.");
        return null;
    }
}
