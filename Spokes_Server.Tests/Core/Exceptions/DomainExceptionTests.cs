using Spokes_Server.Core.Exceptions;

namespace Spokes_Server.Tests.Core.Exceptions;

public class DomainExceptionTests
{
    [Fact]
    public void Constructor_WithMessageAndCode_SetsProperties()
    {
        var ex = new DomainException("Something went wrong", "ERR_CODE");
        Assert.Equal("Something went wrong", ex.Message);
        Assert.Equal("ERR_CODE", ex.ErrorCode);
    }

    [Fact]
    public void Constructor_WithInnerException_SetsInnerExceptionAndCode()
    {
        var inner = new InvalidOperationException("Inner failure");
        var ex = new DomainException("Domain failure", inner, "DOMAIN_ERR");
        Assert.Equal("Domain failure", ex.Message);
        Assert.Same(inner, ex.InnerException);
        Assert.Equal("DOMAIN_ERR", ex.ErrorCode);
    }

    [Fact]
    public void DomainException_InheritsFromInvalidOperationExceptionAndException()
    {
        Assert.True(typeof(InvalidOperationException).IsAssignableFrom(typeof(DomainException)));
        Assert.True(typeof(Exception).IsAssignableFrom(typeof(DomainException)));
    }
}
