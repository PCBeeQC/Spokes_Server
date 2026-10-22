using System;

namespace Spokes_Server.Core.Exceptions;

/// <summary>
/// Thrown when critical, non-recoverable configuration is missing or invalid during server startup.
/// </summary>
public class FatalConfigurationException : InvalidOperationException
{
    public FatalConfigurationException(string message) : base(message)
    {
    }

    public FatalConfigurationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
