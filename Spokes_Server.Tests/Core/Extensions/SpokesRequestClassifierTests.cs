using Microsoft.AspNetCore.Http;
using Spokes_Server.Core.Extensions;

namespace Spokes_Server.Tests.Core.Extensions;

public class SpokesRequestClassifierTests
{
    [Theory]
    [InlineData("/authentication/login", true)]
    [InlineData("/Authentication/Login", true)]
    [InlineData("/authentication/login/", true)]
    [InlineData("/mobile-login", true)]
    [InlineData("/mobile-login-success", false)]
    [InlineData("/access-pending", true)]
    [InlineData("/banned", true)]
    [InlineData("/", false)]
    [InlineData("/chat", false)]
    [InlineData("/session-expired", false)]
    [InlineData("/access-denied", false)]
    [InlineData("/authentication/logout", false)]
    [InlineData("/authentication/login-other", false)]
    [InlineData("", false)]
    public void IsStaticLoginRoute_ClassifiesPublicAuthRoutesAccurately(string rawPath, bool expected)
    {
        var path = string.IsNullOrEmpty(rawPath) ? PathString.Empty : new PathString(rawPath);

        Assert.Equal(expected, SpokesRequestClassifier.IsStaticLoginRoute(path));
    }
}
