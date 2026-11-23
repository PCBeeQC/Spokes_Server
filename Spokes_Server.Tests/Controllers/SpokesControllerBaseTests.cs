using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Spokes_Server.Controllers;
using Xunit;

namespace Spokes_Server.Tests.Controllers;

public class SpokesControllerBaseTests
{
    private class TestController : SpokesControllerBase
    {
    }

    [Fact]
    public void SpokesControllerBase_HasIgnoreAntiforgeryTokenAttribute()
    {
        var attr = typeof(SpokesControllerBase).GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>();
        Assert.NotNull(attr);
    }
}
