using Customers.Domain.Enums;

namespace Customers.Application.DTOs;

public sealed record CustomerAddressDto(
    Guid AddressId, AddressType Type, string Line1, string? Line2, string City, string? Region, string? PostalCode, string Country);

public sealed record CustomerContactDto(Guid ContactId, string Name, string? Email, string? Phone, string? Role);

public sealed record CustomerDto(
    Guid CustomerId, string Code, string Name, string? Email, string? Phone, string? Notes, CustomerStatus Status,
    DateTime CreatedAt, DateTime UpdatedAt,
    IReadOnlyList<CustomerAddressDto> Addresses, IReadOnlyList<CustomerContactDto> Contacts);

/// <summary>List/search row (no child collections: keeps list queries cheap).</summary>
public sealed record CustomerListItemDto(Guid CustomerId, string Code, string Name, string? Email, string? Phone, CustomerStatus Status);

public sealed record CustomerPageDto(IReadOnlyList<CustomerListItemDto> Items, int Total, int Skip, int Take);
