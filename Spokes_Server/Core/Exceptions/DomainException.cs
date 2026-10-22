namespace Spokes_Server.Core.Exceptions;

/// <summary>
/// Base exception for business logic and domain rule violations across Spokes services.
/// Inherits from InvalidOperationException for compatibility with callers catching standard operational exceptions.
/// </summary>
public class DomainException : InvalidOperationException
{
    public string? ErrorCode { get; }

    public DomainException(string message, string? errorCode = null) : base(message)
    {
        ErrorCode = errorCode;
    }

    public DomainException(string message, Exception innerException, string? errorCode = null) : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
