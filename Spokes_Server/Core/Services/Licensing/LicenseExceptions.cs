using Spokes_Server.Core.Exceptions;

namespace Spokes_Server.Core.Services.Licensing;

public class LicenseExpiredException(string message, string errorCode = "LICENSE_EXPIRED") : DomainException(message, errorCode);

/// <summary>
/// The push relay refused a notification with a machine-readable problem code
/// (e.g. TRIAL_EXPIRED, LICENSE_EXPIRED, LICENSE_INVALID).
/// </summary>
public class RelayRejectedException(string message, string errorCode) : DomainException(message, errorCode);

