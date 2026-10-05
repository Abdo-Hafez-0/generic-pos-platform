using Customers.Application.Abstractions;
using Customers.Application.Repositories;
using Customers.Domain.Entities;
using Customers.Domain.Enums;
using Customers.Domain.ValueObjects;
using Platform.Core.Results;

namespace Customers.Application.Commands;

// ============================================================
// CreateCustomer
// ============================================================

public sealed record CreateCustomerCommand(string Code, string Name, string? Email = null, string? Phone = null, string? Notes = null);

public sealed class CreateCustomerCommandHandler(ICustomerRepository repository, ICustomersUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(CreateCustomerCommand command, CancellationToken cancellationToken = default)
    {
        var created = Customer.Create(command.Code, command.Name, command.Email, command.Phone, command.Notes);
        if (created.IsFailure) return Result.Failure<Guid>(created.Error);

        if (await repository.GetByCodeAsync(created.Value.Code, cancellationToken) is not null)
            return Result.Failure<Guid>(Error.Conflict(
                "Customers.CreateCustomer.DuplicateCode", $"A customer with code '{created.Value.Code}' already exists."));

        await repository.AddAsync(created.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(created.Value.Id.Value);
    }
}

// ============================================================
// UpdateCustomer
// ============================================================

public sealed record UpdateCustomerCommand(Guid CustomerId, string Name, string? Email, string? Phone, string? Notes);

public sealed class UpdateCustomerCommandHandler(ICustomerRepository repository, ICustomersUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(UpdateCustomerCommand command, CancellationToken cancellationToken = default)
    {
        var customer = await repository.GetByIdAsync(new CustomerId(command.CustomerId), cancellationToken);
        if (customer is null) return NotFound(command.CustomerId, "UpdateCustomer");

        var result = customer.Update(command.Name, command.Email, command.Phone, command.Notes);
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    internal static Result NotFound(Guid id, string use)
        => Result.Failure(Error.NotFound($"Customers.{use}.CustomerNotFound", $"Customer '{id}' was not found."));
}

// ============================================================
// Deactivate / Reactivate
// ============================================================

public sealed record DeactivateCustomerCommand(Guid CustomerId);

public sealed class DeactivateCustomerCommandHandler(ICustomerRepository repository, ICustomersUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(DeactivateCustomerCommand command, CancellationToken cancellationToken = default)
    {
        var customer = await repository.GetByIdAsync(new CustomerId(command.CustomerId), cancellationToken);
        if (customer is null) return UpdateCustomerCommandHandler.NotFound(command.CustomerId, "DeactivateCustomer");

        var result = customer.Deactivate();
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record ReactivateCustomerCommand(Guid CustomerId);

public sealed class ReactivateCustomerCommandHandler(ICustomerRepository repository, ICustomersUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(ReactivateCustomerCommand command, CancellationToken cancellationToken = default)
    {
        var customer = await repository.GetByIdAsync(new CustomerId(command.CustomerId), cancellationToken);
        if (customer is null) return UpdateCustomerCommandHandler.NotFound(command.CustomerId, "ReactivateCustomer");

        var result = customer.Reactivate();
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

// ============================================================
// Addresses and contacts
// ============================================================

public sealed record AddCustomerAddressCommand(
    Guid CustomerId, AddressType Type, string Line1, string? Line2, string City, string? Region, string? PostalCode, string Country);

public sealed class AddCustomerAddressCommandHandler(ICustomerRepository repository, ICustomersUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(AddCustomerAddressCommand command, CancellationToken cancellationToken = default)
    {
        var customer = await repository.GetByIdAsync(new CustomerId(command.CustomerId), cancellationToken);
        if (customer is null)
            return Result.Failure<Guid>(Error.NotFound("Customers.AddAddress.CustomerNotFound", $"Customer '{command.CustomerId}' was not found."));

        var added = customer.AddAddress(command.Type, command.Line1, command.Line2, command.City, command.Region, command.PostalCode, command.Country);
        if (added.IsFailure) return Result.Failure<Guid>(added.Error);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(added.Value.Id.Value);
    }
}

public sealed record RemoveCustomerAddressCommand(Guid CustomerId, Guid AddressId);

public sealed class RemoveCustomerAddressCommandHandler(ICustomerRepository repository, ICustomersUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(RemoveCustomerAddressCommand command, CancellationToken cancellationToken = default)
    {
        var customer = await repository.GetByIdAsync(new CustomerId(command.CustomerId), cancellationToken);
        if (customer is null) return UpdateCustomerCommandHandler.NotFound(command.CustomerId, "RemoveAddress");

        var result = customer.RemoveAddress(new CustomerAddressId(command.AddressId));
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record AddCustomerContactCommand(Guid CustomerId, string Name, string? Email, string? Phone, string? Role);

public sealed class AddCustomerContactCommandHandler(ICustomerRepository repository, ICustomersUnitOfWork unitOfWork)
{
    public async Task<Result<Guid>> HandleAsync(AddCustomerContactCommand command, CancellationToken cancellationToken = default)
    {
        var customer = await repository.GetByIdAsync(new CustomerId(command.CustomerId), cancellationToken);
        if (customer is null)
            return Result.Failure<Guid>(Error.NotFound("Customers.AddContact.CustomerNotFound", $"Customer '{command.CustomerId}' was not found."));

        var added = customer.AddContact(command.Name, command.Email, command.Phone, command.Role);
        if (added.IsFailure) return Result.Failure<Guid>(added.Error);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(added.Value.Id.Value);
    }
}

public sealed record RemoveCustomerContactCommand(Guid CustomerId, Guid ContactId);

public sealed class RemoveCustomerContactCommandHandler(ICustomerRepository repository, ICustomersUnitOfWork unitOfWork)
{
    public async Task<Result> HandleAsync(RemoveCustomerContactCommand command, CancellationToken cancellationToken = default)
    {
        var customer = await repository.GetByIdAsync(new CustomerId(command.CustomerId), cancellationToken);
        if (customer is null) return UpdateCustomerCommandHandler.NotFound(command.CustomerId, "RemoveContact");

        var result = customer.RemoveContact(new CustomerContactId(command.ContactId));
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
