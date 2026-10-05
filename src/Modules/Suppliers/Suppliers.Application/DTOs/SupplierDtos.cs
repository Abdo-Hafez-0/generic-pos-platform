using Suppliers.Domain.Enums;

namespace Suppliers.Application.DTOs;

public sealed record SupplierAddressDto(
    Guid AddressId, AddressType Type, string Line1, string? Line2, string City, string? Region, string? PostalCode, string Country);

public sealed record SupplierContactDto(Guid ContactId, string Name, string? Email, string? Phone, string? Role);

public sealed record SupplierDto(
    Guid SupplierId, string Code, string Name, string? Email, string? Phone, string? Notes, SupplierStatus Status,
    DateTime CreatedAt, DateTime UpdatedAt,
    IReadOnlyList<SupplierAddressDto> Addresses, IReadOnlyList<SupplierContactDto> Contacts);

/// <summary>List/search row (no child collections: keeps list queries cheap).</summary>
public sealed record SupplierListItemDto(Guid SupplierId, string Code, string Name, string? Email, string? Phone, SupplierStatus Status);

public sealed record SupplierPageDto(IReadOnlyList<SupplierListItemDto> Items, int Total, int Skip, int Take);
