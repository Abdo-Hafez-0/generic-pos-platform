using Catalog.Domain.Enums;

namespace Catalog.Application.DTOs;

/// <summary>Application-level DTO for a product barcode.</summary>
public sealed record BarcodeDto(
    Guid Id,
    string Value,
    BarcodeFormat Format);
