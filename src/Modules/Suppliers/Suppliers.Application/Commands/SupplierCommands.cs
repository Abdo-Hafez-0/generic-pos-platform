using Platform.Application.Abstractions.Authorization;
using Suppliers.Application.Abstractions;
using Suppliers.Application.Repositories;
using Suppliers.Domain.Entities;
using Suppliers.Domain.Enums;
using Suppliers.Domain.ValueObjects;
using Platform.Core.Results;

namespace Suppliers.Application.Commands;

// ============================================================
// CreateSupplier
// ============================================================

public sealed record CreateSupplierCommand(string Code, string Name, string? Email = null, string? Phone = null, string? Notes = null);

public sealed class CreateSupplierCommandHandler(ISupplierRepository repository, ISuppliersUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result<Guid>> HandleAsync(CreateSupplierCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Suppliers.Application.Security.SuppliersCapabilities.ManageSuppliers, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Error);

        var created = Supplier.Create(command.Code, command.Name, command.Email, command.Phone, command.Notes);
        if (created.IsFailure) return Result.Failure<Guid>(created.Error);

        if (await repository.GetByCodeAsync(created.Value.Code, cancellationToken) is not null)
            return Result.Failure<Guid>(Error.Conflict(
                "Suppliers.CreateSupplier.DuplicateCode", $"A supplier with code '{created.Value.Code}' already exists."));

        await repository.AddAsync(created.Value, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(created.Value.Id.Value);
    }
}

// ============================================================
// UpdateSupplier
// ============================================================

public sealed record UpdateSupplierCommand(Guid SupplierId, string Name, string? Email, string? Phone, string? Notes);

public sealed class UpdateSupplierCommandHandler(ISupplierRepository repository, ISuppliersUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(UpdateSupplierCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Suppliers.Application.Security.SuppliersCapabilities.ManageSuppliers, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var supplier = await repository.GetByIdAsync(new SupplierId(command.SupplierId), cancellationToken);
        if (supplier is null) return NotFound(command.SupplierId, "UpdateSupplier");

        var result = supplier.Update(command.Name, command.Email, command.Phone, command.Notes);
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    internal static Result NotFound(Guid id, string use)
        => Result.Failure(Error.NotFound($"Suppliers.{use}.SupplierNotFound", $"Supplier '{id}' was not found."));
}

// ============================================================
// Deactivate / Reactivate
// ============================================================

public sealed record DeactivateSupplierCommand(Guid SupplierId);

public sealed class DeactivateSupplierCommandHandler(ISupplierRepository repository, ISuppliersUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(DeactivateSupplierCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Suppliers.Application.Security.SuppliersCapabilities.ManageSuppliers, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var supplier = await repository.GetByIdAsync(new SupplierId(command.SupplierId), cancellationToken);
        if (supplier is null) return UpdateSupplierCommandHandler.NotFound(command.SupplierId, "DeactivateSupplier");

        var result = supplier.Deactivate();
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record ReactivateSupplierCommand(Guid SupplierId);

public sealed class ReactivateSupplierCommandHandler(ISupplierRepository repository, ISuppliersUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(ReactivateSupplierCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Suppliers.Application.Security.SuppliersCapabilities.ManageSuppliers, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var supplier = await repository.GetByIdAsync(new SupplierId(command.SupplierId), cancellationToken);
        if (supplier is null) return UpdateSupplierCommandHandler.NotFound(command.SupplierId, "ReactivateSupplier");

        var result = supplier.Reactivate();
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

// ============================================================
// Addresses and contacts
// ============================================================

public sealed record AddSupplierAddressCommand(
    Guid SupplierId, AddressType Type, string Line1, string? Line2, string City, string? Region, string? PostalCode, string Country);

public sealed class AddSupplierAddressCommandHandler(ISupplierRepository repository, ISuppliersUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result<Guid>> HandleAsync(AddSupplierAddressCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Suppliers.Application.Security.SuppliersCapabilities.ManageSuppliers, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Error);

        var supplier = await repository.GetByIdAsync(new SupplierId(command.SupplierId), cancellationToken);
        if (supplier is null)
            return Result.Failure<Guid>(Error.NotFound("Suppliers.AddAddress.SupplierNotFound", $"Supplier '{command.SupplierId}' was not found."));

        var added = supplier.AddAddress(command.Type, command.Line1, command.Line2, command.City, command.Region, command.PostalCode, command.Country);
        if (added.IsFailure) return Result.Failure<Guid>(added.Error);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(added.Value.Id.Value);
    }
}

public sealed record RemoveSupplierAddressCommand(Guid SupplierId, Guid AddressId);

public sealed class RemoveSupplierAddressCommandHandler(ISupplierRepository repository, ISuppliersUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(RemoveSupplierAddressCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Suppliers.Application.Security.SuppliersCapabilities.ManageSuppliers, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var supplier = await repository.GetByIdAsync(new SupplierId(command.SupplierId), cancellationToken);
        if (supplier is null) return UpdateSupplierCommandHandler.NotFound(command.SupplierId, "RemoveAddress");

        var result = supplier.RemoveAddress(new SupplierAddressId(command.AddressId));
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record AddSupplierContactCommand(Guid SupplierId, string Name, string? Email, string? Phone, string? Role);

public sealed class AddSupplierContactCommandHandler(ISupplierRepository repository, ISuppliersUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result<Guid>> HandleAsync(AddSupplierContactCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Suppliers.Application.Security.SuppliersCapabilities.ManageSuppliers, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<Guid>(allowed.Error);

        var supplier = await repository.GetByIdAsync(new SupplierId(command.SupplierId), cancellationToken);
        if (supplier is null)
            return Result.Failure<Guid>(Error.NotFound("Suppliers.AddContact.SupplierNotFound", $"Supplier '{command.SupplierId}' was not found."));

        var added = supplier.AddContact(command.Name, command.Email, command.Phone, command.Role);
        if (added.IsFailure) return Result.Failure<Guid>(added.Error);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(added.Value.Id.Value);
    }
}

public sealed record RemoveSupplierContactCommand(Guid SupplierId, Guid ContactId);

public sealed class RemoveSupplierContactCommandHandler(ISupplierRepository repository, ISuppliersUnitOfWork unitOfWork, IAuthorizationService authorization)
{
    public async Task<Result> HandleAsync(RemoveSupplierContactCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(Suppliers.Application.Security.SuppliersCapabilities.ManageSuppliers, cancellationToken);
        if (allowed.IsFailure) return allowed;

        var supplier = await repository.GetByIdAsync(new SupplierId(command.SupplierId), cancellationToken);
        if (supplier is null) return UpdateSupplierCommandHandler.NotFound(command.SupplierId, "RemoveContact");

        var result = supplier.RemoveContact(new SupplierContactId(command.ContactId));
        if (result.IsFailure) return result;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
