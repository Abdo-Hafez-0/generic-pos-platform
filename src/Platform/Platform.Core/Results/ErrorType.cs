namespace Platform.Core.Results;

/// <summary>
/// Classifies the type of an error to enable consistent handling decisions.
/// </summary>
public enum ErrorType
{
    /// <summary>No error (sentinel value used by Result.None).</summary>
    None = 0,

    /// <summary>Input validation failure.</summary>
    Validation = 1,

    /// <summary>Requested resource was not found.</summary>
    NotFound = 2,

    /// <summary>A business rule was violated or a state conflict exists.</summary>
    Conflict = 3,

    /// <summary>A general operational failure.</summary>
    Failure = 4,

    /// <summary>The operation is not authorized for the current user.</summary>
    Unauthorized = 5
}
