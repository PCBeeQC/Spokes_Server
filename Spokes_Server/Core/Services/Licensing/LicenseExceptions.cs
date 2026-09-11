namespace Spokes_Server.Core.Services.Licensing
{
    public class LicenseExpiredException : Exception
    {
        public LicenseExpiredException(string message) : base(message) { }
    }
}
