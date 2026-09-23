namespace Catalog.Application.DTOs;

/// <summary>Application-level DTO for a unit of measurement.</summary>
public sealed record UnitDto(
    Guid Id,
    string Name,
    string Abbreviation,
    bool IsActive);
