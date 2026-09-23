namespace Catalog.Application.DTOs;

/// <summary>Application-level DTO for a category.</summary>
public sealed record CategoryDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsActive,
    DateTime CreatedAt);
