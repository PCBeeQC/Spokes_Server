namespace Spokes_Server.Core.Utilities;

/// <summary>
/// A standardized result wrapper to replace throw/catch control flows across services.
/// Encourages defensive programming and better error propagation to UI.
/// </summary>
public class SpokesResult<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? ErrorMessage { get; }
    
    [System.Text.Json.Serialization.JsonIgnore]
    public Exception? Exception { get; }

    private SpokesResult(bool isSuccess, T? value, string? errorMessage, Exception? exception = null)
    {
        IsSuccess = isSuccess;
        Value = value;
        ErrorMessage = errorMessage;
        Exception = exception;
    }

    public static SpokesResult<T> Success(T value) => new(true, value, null);
    public static SpokesResult<T> Failure(string errorMessage) => new(false, default, errorMessage);
    public static SpokesResult<T> Failure(Exception exception) => new(false, default, exception.Message, exception);
}

public class SpokesResult
{
    public bool IsSuccess { get; }
    public string? ErrorMessage { get; }
    
    [System.Text.Json.Serialization.JsonIgnore]
    public Exception? Exception { get; }

    private SpokesResult(bool isSuccess, string? errorMessage, Exception? exception = null)
    {
        IsSuccess = isSuccess;
        ErrorMessage = errorMessage;
        Exception = exception;
    }

    public static SpokesResult Success() => new(true, null);
    public static SpokesResult Failure(string errorMessage) => new(false, errorMessage);
    public static SpokesResult Failure(Exception exception) => new(false, exception.Message, exception);
}
