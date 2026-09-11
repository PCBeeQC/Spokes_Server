using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Hosting;

namespace Spokes_Server.Core.Middleware;

public class GlobalExceptionFilter : IExceptionFilter
{
    private readonly ILogger<GlobalExceptionFilter> _logger;
    private readonly IHostEnvironment _env;

    public GlobalExceptionFilter(ILogger<GlobalExceptionFilter> logger, IHostEnvironment env)
    {
        _logger = logger;
        _env = env;
    }

    public void OnException(ExceptionContext context)
    {
        _logger.LogError(context.Exception, "Unhandled exception in API Controller.");
        
        context.Result = new ObjectResult(Spokes_Server.Core.Utilities.SpokesResult.Failure(
            _env.IsDevelopment() ? context.Exception.Message : "An unexpected error occurred. Please contact support."))
        {
            StatusCode = 500
        };
        
        context.ExceptionHandled = true;
    }
}
