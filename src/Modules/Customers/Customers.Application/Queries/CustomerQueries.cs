using Customers.Application.DTOs;
using Customers.Application.Repositories;
using Customers.Domain.Entities;
using Customers.Domain.Enums;
using Customers.Domain.ValueObjects;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;

namespace Customers.Application.Queries;

internal static class CustomerMapping
{
    public static CustomerDto ToDto(this Customer c) => new(
        c.Id.Value, c.Code, c.Name, c.Email, c.Phone, c.Notes, c.Status, c.CreatedAt, c.UpdatedAt,
        c.Addresses.Select(a => new CustomerAddressDto(a.Id.Value, a.Type, a.Line1, a.Line2, a.City, a.Region, a.PostalCode, a.Country)).ToList(),
        c.Contacts.Select(x => new CustomerContactDto(x.Id.Value, x.Name, x.Email, x.Phone, x.Role)).ToList());

    public static CustomerListItemDto ToListItem(this Customer c) => new(c.Id.Value, c.Code, c.Name, c.Email, c.Phone, c.Status);
}

// ---- GetCustomerById

public sealed record GetCustomerByIdQuery(Guid CustomerId);

public sealed class GetCustomerByIdQueryHandler(ICustomerRepository repository, IAuthorizationService authorization)
{
    /// <summary>Success with a null value means "no such customer"; a failure means the caller may not view customers.</summary>
    public async Task<Result<CustomerDto?>> HandleAsync(GetCustomerByIdQuery query, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Customers.Application.Security.CustomersCapabilities.ViewCustomers, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<CustomerDto?>(allowed.Error);

        return Result.Success((await repository.GetByIdAsync(new CustomerId(query.CustomerId), cancellationToken))?.ToDto());
    }
}

// ---- ListCustomers (paged)

public sealed record ListCustomersQuery(int Skip = 0, int Take = 50, CustomerStatus? Status = null);

public sealed class ListCustomersQueryHandler(ICustomerRepository repository, IAuthorizationService authorization)
{
    public const int MaxPageSize = 200;

    public async Task<Result<CustomerPageDto>> HandleAsync(ListCustomersQuery query, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Customers.Application.Security.CustomersCapabilities.ViewCustomers, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<CustomerPageDto>(allowed.Error);

        var skip = Math.Max(0, query.Skip);
        var take = Math.Clamp(query.Take, 1, MaxPageSize);

        var items = await repository.ListAsync(skip, take, query.Status, cancellationToken);
        var total = await repository.CountAsync(query.Status, cancellationToken);
        return Result.Success(new CustomerPageDto(items.Select(c => c.ToListItem()).ToList(), total, skip, take));
    }
}

// ---- SearchCustomers

public sealed record SearchCustomersQuery(string Text, int Take = 25);

public sealed class SearchCustomersQueryHandler(ICustomerRepository repository, IAuthorizationService authorization)
{
    public async Task<Result<IReadOnlyList<CustomerListItemDto>>> HandleAsync(SearchCustomersQuery query, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Customers.Application.Security.CustomersCapabilities.ViewCustomers, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<IReadOnlyList<CustomerListItemDto>>(allowed.Error);

        if (string.IsNullOrWhiteSpace(query.Text)) return Result.Success<IReadOnlyList<CustomerListItemDto>>([]);

        var found = await repository.SearchAsync(query.Text.Trim(), Math.Clamp(query.Take, 1, ListCustomersQueryHandler.MaxPageSize), cancellationToken);
        return Result.Success<IReadOnlyList<CustomerListItemDto>>(found.Select(c => c.ToListItem()).ToList());
    }
}
