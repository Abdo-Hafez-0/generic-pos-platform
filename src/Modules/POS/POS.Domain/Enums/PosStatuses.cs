namespace POS.Domain.Enums;

/// <summary>Lifecycle of a cashier session.</summary>
public enum PosSessionStatus
{
    /// <summary>Session is open; carts may be started and sold from.</summary>
    Open = 1,

    /// <summary>Session is closed (terminal).</summary>
    Closed = 2
}

/// <summary>Lifecycle of a POS cart (the transaction being built).</summary>
public enum PosCartStatus
{
    /// <summary>Cart can be edited.</summary>
    Open = 1,

    /// <summary>Cart was handed to Sales and the sale completed (terminal).</summary>
    CheckedOut = 2
}
