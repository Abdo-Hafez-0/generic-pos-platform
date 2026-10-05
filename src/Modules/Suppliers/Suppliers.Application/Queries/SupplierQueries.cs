using Suppliers.Application.DTOs;
using Suppliers.Application.Repositories;
using Suppliers.Domain.Entities;
using Suppliers.Domain.Enums;
using Suppliers.Domain.ValueObjects;

namespace Suppliers.Application.Queries;

internal static class SupplierMapping
{
    public static SupplierDto ToDto(this Supplier c) => new(
        c.Id.Value, c.Code, c.Name, c.Email, c.Phone, c.Notes, c.Status, c.CreatedAt, c.UpdatedAt,
        c.Addresses.Select(a => new SupplierAddressDto(a.Id.Value, a.Type, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.Country)).ToList(),
        c.Contacts.Select(x => new SupplierContactDto(x.Id.Value, x.Name, x.Email, x.Phone, x.Role)).ToList());

    public static SupplierListItemDto ToListItem(this Supplier c) => new(c.Id.Value, c.Code, c.Name, c.Email, c.Phone, c.Status);
}

// ---- GetSupplierById

public sealed record GetSupplierByIdQuery(Guid SupplierId);

public sealed class GetSupplierByIdQueryHandler(ISupplierRepository repository)
{
    public async Task<SupplierDto?> HandleAsync(GetSupplierByIdQuery query, CancellationToken cancellationToken = default)
        => (await repository.GetByIdAsync(new SupplierId(query.SupplierId), cancellationToken))?.ToDto();
}

// ---- ListSuppliers (paged)

public sealed record ListSuppliersQuery(int Skip = 0, int Take = 50, SupplierStatus? Status = null);

public sealed class ListSuppliersQueryHandler(ISupplierRepository repository)
{
    public const int MaxPageSize = 200;

    public async Task<SupplierPageDto> HandleAsync(ListSuppliersQuery query, CancellationToken cancellationToken = default)
    {
        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take, 1, MaxPageSize);

        var items = await repository.ListAsync(skip, take, query.Status, cancellationToken);
        var total = await repository.CountAsync(query.Status, cancellationToken);
        return new SupplierPageDto(items.Select(c => c.ToListItem()).ToList(), total, skip, take);
    }
}

// ---- SearchSuppliers

public sealed record SearchSuppliersQuery(string Text, int Take = 25);

public sealed class SearchSuppliersQueryHandler(ISupplierRepository repository)
{
    public async Task<IReadOnlyList<SupplierListItemDto>> HandleAsync(SearchSuppliersQuery query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.Text)) return [];

        var found = await repository.SearchAsync(query.Text.Trim(), Math.Clamp(query.Take, 1, ListSuppliersQueryHandler.MaxPageSize), cancellationToken);
        return found.Select(c => c.ToListItem()).ToList();
    }
}
