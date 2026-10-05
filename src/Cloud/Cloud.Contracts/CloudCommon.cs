namespace Cloud.Contracts;

/// <summary>The body of every failed server API response.</summary>
public sealed record ApiError(string Code, string Message);

/// <summary>Stable error codes of the Stage 9 server APIs.</summary>
public static class CloudErrorCodes
{
    public const string Validation = "Cloud.Validation";
    public const string Unauthorized = "Cloud.Unauthorized";
    public const string Forbidden = "Cloud.Forbidden";
    public const string NotFound = "Cloud.NotFound";
    public const string Conflict = "Cloud.Conflict";
    public const string InvalidState = "Cloud.InvalidState";
    public const string TooLarge = "Cloud.TooLarge";

    /// <summary>Too many failed authentications from one caller: it is refused for a while (HTTP 429).</summary>
    public const string TooManyRequests = "Cloud.TooManyRequests";

    /// <summary>The request arrived over plain HTTP outside Development: it is refused (HTTP 403) and never served.</summary>
    public const string HttpsRequired = "Cloud.HttpsRequired";

    public const string InvalidPackage = "Package.Invalid";
    public const string PackageSignatureRejected = "Package.SignatureRejected";
    public const string BackupHashMismatch = "Backup.HashMismatch";

    /// <summary>Maps an error code to the HTTP status an API should answer with.</summary>
    public static int ToHttpStatus(string code) => code switch
    {
        Unauthorized => 401,
        Forbidden => 403,
        NotFound => 404,
        Conflict or InvalidState => 409,
        TooLarge => 413,
        TooManyRequests => 429,
        _ => 400
    };
}

/// <summary>The outcome of a server application operation: a value or an error, never an exception for expected failures.</summary>
public class ServiceResult
{
    protected ServiceResult(ApiError? error) => Error = error;

    public ApiError? Error { get; }

    public bool IsSuccess => Error is null;

    public static ServiceResult Ok() => new(null);

    public static ServiceResult Fail(string code, string message) => new(new ApiError(code, message));
}

/// <summary><see cref="ServiceResult"/> carrying a value on success.</summary>
public sealed class ServiceResult<T> : ServiceResult
{
    private ServiceResult(T? value, ApiError? error) : base(error) => Value = value;

    public T? Value { get; }

    public static ServiceResult<T> Ok(T value) => new(value, null);

    public new static ServiceResult<T> Fail(string code, string message) => new(default, new ApiError(code, message));

    public static ServiceResult<T> From(ApiError error) => new(default, error);
}

/// <summary>One page of a larger result.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

/// <summary>Paging rules shared by every list endpoint.</summary>
public static class Paging
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static (int Page, int PageSize) Normalize(int? page, int? pageSize)
        => (Math.Max(page ?? 1, 1), Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize));
}
