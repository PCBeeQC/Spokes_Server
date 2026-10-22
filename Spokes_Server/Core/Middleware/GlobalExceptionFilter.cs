using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spokes_Server.Core.Exceptions;
using Spokes_Server.Core.Helpers;

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
        if (context.Exception is DomainException domainEx)
        {
            _logger.LogWarning(domainEx, "Domain exception in API Controller: {ErrorCode} - {Message}", domainEx.ErrorCode, domainEx.Message);

            context.Result = new ObjectResult(SpokesResult.Failure(domainEx.Message, domainEx.ErrorCode))
            {
                StatusCode = 400
            };

            context.ExceptionHandled = true;
            return;
        }

        _logger.LogError(context.Exception, "Unhandled exception in API Controller.");
        
        context.Result = new ObjectResult(SpokesResult.Failure(
            _env.IsDevelopment() ? context.Exception.Message : "An unexpected error occurred. Please contact support."))
        {
            StatusCode = 500
        };
        
        context.ExceptionHandled = true;
    }
}
