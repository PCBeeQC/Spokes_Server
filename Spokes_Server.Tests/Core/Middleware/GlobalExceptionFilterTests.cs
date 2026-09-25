using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Spokes_Server.Core.Middleware;
using Spokes_Server.Core.Utilities;

namespace Spokes_Server.Tests.Core.Middleware;

public class GlobalExceptionFilterTests
{
    private readonly Mock<ILogger<GlobalExceptionFilter>> _mockLogger;
    private readonly Mock<IHostEnvironment> _mockEnv;

    public GlobalExceptionFilterTests()
    {
        _mockLogger = new Mock<ILogger<GlobalExceptionFilter>>();
        _mockEnv = new Mock<IHostEnvironment>();
    }

    private static ExceptionContext CreateExceptionContext(Exception exception)
    {
        var actionContext = new ActionContext(
            new DefaultHttpContext(),
            new RouteData(),
            new ActionDescriptor());

        return new ExceptionContext(actionContext, new List<IFilterMetadata>())
        {
            Exception = exception
        };
    }

    [Fact]
    public void OnException_WhenDevelopmentEnvironment_Returns500WithExceptionMessageAndMarksHandled()
    {
        // Arrange
        _mockEnv.Setup(e => e.EnvironmentName).Returns(Environments.Development);
        var filter = new GlobalExceptionFilter(_mockLogger.Object, _mockEnv.Object);
        var exception = new InvalidOperationException("Detailed database connection failure");
        var context = CreateExceptionContext(exception);

        // Act
        filter.OnException(context);

        // Assert
        Assert.True(context.ExceptionHandled);
        var objectResult = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(500, objectResult.StatusCode);

        var spokesResult = Assert.IsType<SpokesResult>(objectResult.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Detailed database connection failure", spokesResult.ErrorMessage);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Unhandled exception in API Controller.")),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void OnException_WhenProductionEnvironment_Returns500WithGenericMessageAndMarksHandled()
    {
        // Arrange
        _mockEnv.Setup(e => e.EnvironmentName).Returns(Environments.Production);
        var filter = new GlobalExceptionFilter(_mockLogger.Object, _mockEnv.Object);
        var exception = new InvalidOperationException("Sensitive internal database connection string");
        var context = CreateExceptionContext(exception);

        // Act
        filter.OnException(context);

        // Assert
        Assert.True(context.ExceptionHandled);
        var objectResult = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(500, objectResult.StatusCode);

        var spokesResult = Assert.IsType<SpokesResult>(objectResult.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("An unexpected error occurred. Please contact support.", spokesResult.ErrorMessage);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Unhandled exception in API Controller.")),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void OnException_WhenStagingEnvironment_Returns500WithGenericMessage()
    {
        // Arrange
        _mockEnv.Setup(e => e.EnvironmentName).Returns(Environments.Staging);
        var filter = new GlobalExceptionFilter(_mockLogger.Object, _mockEnv.Object);
        var exception = new Exception("Staging internal error");
        var context = CreateExceptionContext(exception);

        // Act
        filter.OnException(context);

        // Assert
        Assert.True(context.ExceptionHandled);
        var objectResult = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(500, objectResult.StatusCode);

        var spokesResult = Assert.IsType<SpokesResult>(objectResult.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("An unexpected error occurred. Please contact support.", spokesResult.ErrorMessage);
    }

    [Fact]
    public void OnException_WithInnerException_LogsExceptionWithInnerDetailsAndMarksHandled()
    {
        // Arrange
        _mockEnv.Setup(e => e.EnvironmentName).Returns(Environments.Development);
        var filter = new GlobalExceptionFilter(_mockLogger.Object, _mockEnv.Object);
        var innerException = new FormatException("Invalid JSON payload format");
        var outerException = new InvalidOperationException("Failed processing request", innerException);
        var context = CreateExceptionContext(outerException);

        // Act
        filter.OnException(context);

        // Assert
        Assert.True(context.ExceptionHandled);
        var objectResult = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(500, objectResult.StatusCode);

        var spokesResult = Assert.IsType<SpokesResult>(objectResult.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Failed processing request", spokesResult.ErrorMessage);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.Is<Exception>(ex => ex == outerException && ex.InnerException == innerException),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void OnException_WithCustomExceptionType_HandlesProperlyInDevelopment()
    {
        // Arrange
        _mockEnv.Setup(e => e.EnvironmentName).Returns(Environments.Development);
        var filter = new GlobalExceptionFilter(_mockLogger.Object, _mockEnv.Object);
        var customException = new CustomTestException("Custom domain rule violated", 4001);
        var context = CreateExceptionContext(customException);

        // Act
        filter.OnException(context);

        // Assert
        Assert.True(context.ExceptionHandled);
        var objectResult = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(500, objectResult.StatusCode);

        var spokesResult = Assert.IsType<SpokesResult>(objectResult.Value);
        Assert.False(spokesResult.IsSuccess);
        Assert.Equal("Custom domain rule violated", spokesResult.ErrorMessage);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                customException,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    private class CustomTestException : Exception
    {
        public int ErrorCode { get; }

        public CustomTestException(string message, int errorCode) : base(message)
        {
            ErrorCode = errorCode;
        }
    }
}
