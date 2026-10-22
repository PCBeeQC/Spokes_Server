using System.Text.Json.Serialization;

namespace Spokes_Server.Core.Helpers;

/// <summary>
/// A standardized result and transport envelope used across controllers, middleware, and domain services.
/// Non-2xx error responses are standardized across all API controllers using SpokesResult.Failure
/// via <c>SpokesControllerBase</c> helper methods, providing a uniform error contract
/// for HTTP and mobile clients. For successful HTTP responses, endpoints adhere to standard REST
/// conventions: resource queries return domain entities or collections directly, file endpoints
/// return streaming file payloads, and mutating commands return either typed outcome models or
/// standard HTTP acknowledgments. In domain services (such as <c>ChatService</c> and <c>AlbumService</c>),
/// operations that perform authorization, validation, or moderation checks return <c>SpokesResult</c>
/// or <c>SpokesResult&lt;T&gt;</c> to convey structured success/failure outcomes and error reason codes to callers
/// without exception overhead.
/// </summary>
public class SpokesResult<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? ErrorMessage { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; }

    [JsonPropertyName("error")]
    public string? Error => ErrorMessage;
    
    [JsonIgnore]
    public Exception? Exception { get; }

    private SpokesResult(bool isSuccess, T? value, string? errorMessage, string? code = null, Exception? exception = null)
    {
        IsSuccess = isSuccess;
        Value = value;
        ErrorMessage = errorMessage;
        Code = code;
        Exception = exception;
    }

    public static SpokesResult<T> Success(T value) => new(true, value, null);
    public static SpokesResult<T> Failure(string errorMessage, string? code = null) => new(false, default, errorMessage, code);
    public static SpokesResult<T> Failure(Exception exception, string? code = null) => new(false, default, exception.Message, code, exception);

    public static implicit operator T?(SpokesResult<T> result) => result != null ? result.Value : default;

    public override string ToString()
    {
        if (Code != null && ErrorMessage != null) return $"{ErrorMessage} ({Code})";
        return ErrorMessage ?? (IsSuccess ? "Success" : "Failure");
    }
}

/// <summary>
/// A standardized result and transport envelope used across controllers, middleware, and domain services.
/// Non-2xx error responses are standardized across all API controllers using SpokesResult.Failure
/// via <c>SpokesControllerBase</c> helper methods, providing a uniform error contract
/// for HTTP and mobile clients. For successful HTTP responses, endpoints adhere to standard REST
/// conventions: resource queries return domain entities or collections directly, file endpoints
/// return streaming file payloads, and mutating commands return either typed outcome models or
/// standard HTTP acknowledgments. In domain services (such as <c>ChatService</c> and <c>AlbumService</c>),
/// operations that perform authorization, validation, or moderation checks return <c>SpokesResult</c>
/// or <c>SpokesResult&lt;T&gt;</c> to convey structured success/failure outcomes and error reason codes to callers
/// without exception overhead.
/// </summary>
public class SpokesResult
{
    public bool IsSuccess { get; }
    public string? ErrorMessage { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; }

    [JsonPropertyName("error")]
    public string? Error => ErrorMessage;
    
    [JsonIgnore]
    public Exception? Exception { get; }

    private SpokesResult(bool isSuccess, string? errorMessage, string? code = null, Exception? exception = null)
    {
        IsSuccess = isSuccess;
        ErrorMessage = errorMessage;
        Code = code;
        Exception = exception;
    }

    public static SpokesResult Success() => new(true, null);
    public static SpokesResult Failure(string errorMessage, string? code = null) => new(false, errorMessage, code);
    public static SpokesResult Failure(Exception exception, string? code = null) => new(false, exception.Message, code, exception);

    public static implicit operator bool(SpokesResult result) => result != null && result.IsSuccess;

    public override string ToString()
    {
        if (Code != null && ErrorMessage != null) return $"{ErrorMessage} ({Code})";
        return ErrorMessage ?? (IsSuccess ? "Success" : "Failure");
    }
}
