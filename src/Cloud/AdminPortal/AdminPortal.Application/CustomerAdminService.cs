using Cloud.Contracts;
using Cloud.Contracts.Admin;

namespace AdminPortal.Application;

/// <summary>Input checks shared by the administration services.</summary>
internal static class Check
{
    public static string? Text(string? value, string field, int maxLength, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
            return required ? $"{field} is required." : null;

        return value.Trim().Length > maxLength ? $"{field} may not exceed {maxLength} characters." : null;
    }

    public static string? Email(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        var e = email.Trim();
        var at = e.IndexOf('@');
        return e.Length > 254 || at < 1 || at != e.LastIndexOf('@') || at == e.Length - 1 || e.Any(char.IsWhiteSpace)
            ? "Email is not a valid address."
            : null;
    }

    public static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class AdminMapping
{
    public static CustomerDto ToDto(this Customer c)
        => new(c.Id, c.Name, c.ContactName, c.Email, c.Phone, c.Notes, c.IsActive, c.CreatedAt, c.UpdatedAt);
}

/// <summary>Vendor customer management: create, update, deactivate/reactivate, query.</summary>
public sealed class CustomerAdminService(ICustomerRepository customers, AdminAuditRecorder audit, TimeProvider timeProvider)
{
    public async Task<ServiceResult<CustomerDto>> CreateAsync(AdminActor actor, CustomerRequest request, CancellationToken cancellationToken = default)
    {
        var invalid = Validate(request);
        if (invalid is not null)
            return ServiceResult<CustomerDto>.Fail(CloudErrorCodes.Validation, invalid);

        var name = request.Name.Trim();
        if (await customers.NameExistsAsync(name, null, cancellationToken))
            return ServiceResult<CustomerDto>.Fail(CloudErrorCodes.Conflict, $"A customer named '{name}' already exists.");

        var now = timeProvider.GetUtcNow();
        var customer = new Customer { Id = Guid.NewGuid(), IsActive = true, CreatedAt = now, UpdatedAt = now };
        Apply(customer, request);

        await customers.AddAsync(customer, cancellationToken);
        await audit.RecordAsync(actor, "customer.create", "customer", customer.Id.ToString(), $"Created customer '{customer.Name}'.", cancellationToken);
        return ServiceResult<CustomerDto>.Ok(customer.ToDto());
    }

    public async Task<ServiceResult<CustomerDto>> UpdateAsync(AdminActor actor, Guid id, CustomerRequest request, CancellationToken cancellationToken = default)
    {
        var invalid = Validate(request);
        if (invalid is not null)
            return ServiceResult<CustomerDto>.Fail(CloudErrorCodes.Validation, invalid);

        var customer = await customers.FindAsync(id, cancellationToken);
        if (customer is null)
            return NotFound();

        var name = request.Name.Trim();
        if (await customers.NameExistsAsync(name, id, cancellationToken))
            return ServiceResult<CustomerDto>.Fail(CloudErrorCodes.Conflict, $"A customer named '{name}' already exists.");

        Apply(customer, request);
        customer.UpdatedAt = timeProvider.GetUtcNow();

        await customers.SaveAsync(customer, cancellationToken);
        await audit.RecordAsync(actor, "customer.update", "customer", id.ToString(), $"Updated customer '{customer.Name}'.", cancellationToken);
        return ServiceResult<CustomerDto>.Ok(customer.ToDto());
    }

    /// <summary>Deactivation blocks NEW licenses for the customer; existing licenses and data are untouched.</summary>
    public Task<ServiceResult<CustomerDto>> DeactivateAsync(AdminActor actor, Guid id, CancellationToken cancellationToken = default)
        => SetActiveAsync(actor, id, false, cancellationToken);

    public Task<ServiceResult<CustomerDto>> ReactivateAsync(AdminActor actor, Guid id, CancellationToken cancellationToken = default)
        => SetActiveAsync(actor, id, true, cancellationToken);

    public async Task<ServiceResult<CustomerDto>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var customer = await customers.FindAsync(id, cancellationToken);
        return customer is null ? NotFound() : ServiceResult<CustomerDto>.Ok(customer.ToDto());
    }

    public async Task<PagedResult<CustomerDto>> ListAsync(string? search, bool includeInactive, int? page, int? pageSize, CancellationToken cancellationToken = default)
    {
        var (p, size) = Paging.Normalize(page, pageSize);
        var result = await customers.ListAsync(Check.Trimmed(search), includeInactive, p, size, cancellationToken);
        return new PagedResult<CustomerDto>(result.Items.Select(c => c.ToDto()).ToList(), p, size, result.Total);
    }

    private async Task<ServiceResult<CustomerDto>> SetActiveAsync(AdminActor actor, Guid id, bool active, CancellationToken cancellationToken)
    {
        var customer = await customers.FindAsync(id, cancellationToken);
        if (customer is null)
            return NotFound();

        if (customer.IsActive == active)
            return ServiceResult<CustomerDto>.Fail(CloudErrorCodes.InvalidState, active ? "The customer is already active." : "The customer is already inactive.");

        customer.IsActive = active;
        customer.UpdatedAt = timeProvider.GetUtcNow();

        await customers.SaveAsync(customer, cancellationToken);
        await audit.RecordAsync(actor, active ? "customer.reactivate" : "customer.deactivate", "customer", id.ToString(),
            $"{(active ? "Reactivated" : "Deactivated")} customer '{customer.Name}'.", cancellationToken);
        return ServiceResult<CustomerDto>.Ok(customer.ToDto());
    }

    private static string? Validate(CustomerRequest? r)
        => r is null ? "A request body is required."
         : Check.Text(r.Name, "Name", 200, true)
        ?? Check.Text(r.ContactName, "Contact name", 200, false)
        ?? Check.Email(r.Email)
        ?? Check.Text(r.Phone, "Phone", 50, false)
        ?? Check.Text(r.Notes, "Notes", 2000, false);

    private static void Apply(Customer customer, CustomerRequest r)
    {
        customer.Name = r.Name.Trim();
        customer.ContactName = Check.Trimmed(r.ContactName);
        customer.Email = Check.Trimmed(r.Email);
        customer.Phone = Check.Trimmed(r.Phone);
        customer.Notes = Check.Trimmed(r.Notes);
    }

    private static ServiceResult<CustomerDto> NotFound()
        => ServiceResult<CustomerDto>.Fail(CloudErrorCodes.NotFound, "Unknown customer.");
}
