namespace Sales.Contracts.Models;

/// <summary>A completed sale reduced to what reports need (FIX-12): when it was completed (UTC) and its totals.</summary>
public sealed record CompletedSaleResult(Guid SaleId, DateTime CompletedAt, decimal GrandTotal, decimal TaxTotal);
