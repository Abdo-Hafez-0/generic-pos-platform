namespace Sales.Domain.Enums;

/// <summary>
/// Defines the status of a Return within the Sales module.
///
/// Architecture reference: Module Map §17 (Sales Owns: Return, ReturnItem).
/// </summary>
public enum ReturnStatus
{
    /// <summary>Return request is being created.</summary>
    Pending = 1,

    /// <summary>Return has been accepted and processed.</summary>
    Processed = 2,

    /// <summary>Return request was rejected.</summary>
    Rejected = 3
}
