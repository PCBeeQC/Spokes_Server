namespace Spokes_Server.Core.Exceptions;

public class InvalidMasterPasswordException : DomainException
{
    public InvalidMasterPasswordException(string message) 
        : base(message, "INVALID_MASTER_PASSWORD")
    {
    }

    public InvalidMasterPasswordException(string message, Exception innerException) 
        : base(message, innerException, "INVALID_MASTER_PASSWORD")
    {
    }
}
