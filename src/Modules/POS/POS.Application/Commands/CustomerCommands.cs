using Customers.Contracts.Interfaces;
using Customers.Contracts.Models;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using POS.Application.Abstractions;
using POS.Application.Repositories;
using POS.Contracts.Models;
using POS.Domain.Enums;
using POS.Domain.ValueObjects;

namespace POS.Application.Commands;

// ============================================================
// A customer on the sale (FIX-11)
// ============================================================

/// <summary>
/// User decisions (2026-10-09): anyone who may sell (pos.sale.create) can find and attach an existing ACTIVE customer; the till sees only code
/// and name, never contact details (Stage 11: cashiers do not read customer records); customers are not created at the till. Customers is
/// OPTIONAL: without it the till says so and sells without customers.
/// </summary>
internal static class PosCustomers
{
    public static Error Unavailable() => Error.Conflict("POS.Customer.Unavailable", "Customers are not available on this installation (the Customers module is not installed).");
}

public sealed record FindSaleCustomersQuery(string Text);

public sealed class FindSaleCustomersQueryHandler(IAuthorizationService authorization, ICustomerReader? customers = null)
{
    public const int MaxResults = 20;

    public async Task<Result<IReadOnlyList<POSCustomerResult>>> HandleAsync(FindSaleCustomersQuery query, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POS.Application.Security.POSCapabilities.CreateSale, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<IReadOnlyList<POSCustomerResult>>(allowed.Error);
        if (customers is null) return Result.Failure<IReadOnlyList<POSCustomerResult>>(PosCustomers.Unavailable());
        if (string.IsNullOrWhiteSpace(query.Text) || query.Text.Trim().Length < 2)
            return Result.Failure<IReadOnlyList<POSCustomerResult>>(Error.Validation("POS.Customer.SearchTooShort", "Type at least 2 characters of the customer's code, name or phone."));

        var found = await customers.SearchAsync(query.Text.Trim(), 50, cancellationToken);
        IReadOnlyList<POSCustomerResult> shown = found
            .Where(c => c.Status == CustomerStatusContract.Active)
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxResults)
            .Select(c => new POSCustomerResult(c.CustomerId, c.Code, c.Name))   // code and name only
            .ToList();
        return Result.Success(shown);
    }
}

public sealed record SetSaleCustomerCommand(Guid CartId, Guid? CustomerId);

public sealed class SetSaleCustomerCommandHandler(
    IPosCartRepository cartRepository,
    IPosUnitOfWork unitOfWork,
    IAuthorizationService authorization,
    ICustomerLookup? customers = null)
{
    public async Task<Result> HandleAsync(SetSaleCustomerCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POS.Application.Security.POSCapabilities.CreateSale, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var cart = await cartRepository.GetByIdAsync(new PosCartId(command.CartId), cancellationToken);
        if (cart is null)
            return Result.Failure(Error.NotFound("POS.Customer.CartNotFound", $"Cart '{command.CartId}' was not found."));
        if (cart.Status != PosCartStatus.Open)
            return Result.Failure(Error.Conflict("POS.Customer.CartNotOpen", "The customer can only be chosen on an open cart."));

        Result set;
        if (command.CustomerId is { } customerId)
        {
            if (customers is null) return Result.Failure(PosCustomers.Unavailable());

            var customer = await customers.FindByIdAsync(customerId, cancellationToken);
            if (customer is null)
                return Result.Failure(Error.NotFound("POS.Customer.NotFound", "That customer was not found."));
            if (customer.Status != CustomerStatusContract.Active)
                return Result.Failure(Error.Conflict("POS.Customer.Inactive", $"Customer {customer.Code} is not active and cannot be chosen."));

            set = cart.SetCustomer(customer.CustomerId, customer.Code, customer.Name);
        }
        else
        {
            set = cart.SetCustomer(null);
        }

        if (set.IsFailure) return set;
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
