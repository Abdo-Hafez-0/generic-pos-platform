namespace Platform.Core.Results;

/// <summary>
/// Represents an error that can occur during an operation.
/// Errors are structured data, not exceptions. Use exceptions for truly exceptional/unexpected failures.
/// </summary>
public sealed class Error : IEquatable<Error>
{
    /// <summary>
    /// Machine-readable error code. Convention: "Domain.ErrorName" (e.g., "Catalog.ProductNotFound").
    /// </summary>
    public string Code { get; }

    /// <summary>
    /// Human-readable error description.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Classifies the error type for UI/handling decisions.
    /// </summary>
    public ErrorType Type { get; }

    private Error(string code, string description, ErrorType type)
    {
        Code = code;
        Description = description;
        Type = type;
    }

    /// <summary>Creates a validation error.</summary>
    public static Error Validation(string code, string description) =>
        new(code, description, ErrorType.Validation);

    /// <summary>Creates a not-found error.</summary>
    public static Error NotFound(string code, string description) =>
        new(code, description, ErrorType.NotFound);

    /// <summary>Creates a business rule conflict error.</summary>
    public static Error Conflict(string code, string description) =>
        new(code, description, ErrorType.Conflict);

    /// <summary>Creates a general failure error.</summary>
    public static Error Failure(string code, string description) =>
        new(code, description, ErrorType.Failure);

    /// <summary>Creates an unauthorized access error.</summary>
    public static Error Unauthorized(string code, string description) =>
        new(code, description, ErrorType.Unauthorized);

    /// <summary>
    /// A sentinel value representing no error. Used internally by Result.
    /// </summary>
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.None);

    public bool Equals(Error? other) =>
        other is not null && Code == other.Code && Type == other.Type;

    public override bool Equals(object? obj) => Equals(obj as Error);

    public override int GetHashCode() => HashCode.Combine(Code, Type);

    public override string ToString() => $"[{Type}] {Code}: {Description}";

    public static bool operator ==(Error? left, Error? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(Error? left, Error? right) => !(left == right);
}
