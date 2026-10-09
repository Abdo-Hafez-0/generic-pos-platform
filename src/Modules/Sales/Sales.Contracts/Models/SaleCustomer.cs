namespace Sales.Contracts.Models;

/// <summary>
/// The customer of a sale as the caller (POS) knows it at the moment of the sale (FIX-11). Sales stores it as a snapshot and never asks the
/// Customers module about it.
/// </summary>
public sealed record SaleCustomer(Guid CustomerId, string Code, string Name);
