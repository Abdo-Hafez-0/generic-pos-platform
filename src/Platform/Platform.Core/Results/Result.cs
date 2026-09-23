namespace Platform.Core.Results;

/// <summary>
/// Represents the outcome of an operation that may succeed or fail.
/// Avoids using exceptions for expected business failures, keeping control flow explicit.
///
/// Usage:
///   Result result = Result.Success();
///   Result result = Result.Failure(Error.NotFound("Catalog.ProductNotFound", "Product was not found."));
///
///   if (result.IsSuccess) { ... }
///   if (result.IsFailure) { var error = result.Error; }
/// </summary>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None)
            throw new InvalidOperationException("A successful result cannot contain an error.");

        if (!isSuccess && error == Error.None)
            throw new InvalidOperationException("A failure result must contain an error.");

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }
    public bool IsFailure => !IsSuccess;
    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);

    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);

    public static implicit operator Result(Error error) => Failure(error);
}

/// <summary>
/// Represents the outcome of an operation that returns a value on success.
/// </summary>
/// <typeparam name="TValue">The type of the success value.</typeparam>
public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    /// <summary>
    /// The success value. Only valid when IsSuccess is true.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if accessed on a failure result.</exception>
    public TValue Value =>
        IsSuccess
            ? _value!
            : throw new InvalidOperationException("Cannot access Value of a failed Result.");

    public static implicit operator Result<TValue>(TValue value) => Success(value);

    public static implicit operator Result<TValue>(Error error) => Failure<TValue>(error);
}
