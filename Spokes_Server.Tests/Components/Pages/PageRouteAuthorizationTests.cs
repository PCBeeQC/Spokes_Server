using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Spokes_Server.Components.Pages.Chat;
using Spokes_Server.Components.Pages.HR;
using Spokes_Server.Components.Pages.Planning;
using Spokes_Server.Core.Constants;
using Xunit;

namespace Spokes_Server.Tests.Components.Pages;

public class PageRouteAuthorizationTests
{
    [Theory]
    [InlineData(typeof(Spokes_Server.Components.Pages.Chat.Chat), AppPermissions.Chat.Use)]
    [InlineData(typeof(MyCalendar), AppPermissions.Calendar.View)]
    [InlineData(typeof(MyTimesheet), AppPermissions.Timesheets.AccessTimesheet)]
    public void Page_HasDeclaredAuthorizePolicy(Type pageType, string expectedPolicy)
    {
        var attributes = pageType.GetCustomAttributes<AuthorizeAttribute>(inherit: true).ToList();
        Assert.NotEmpty(attributes);
        Assert.Contains(attributes, attr => attr.Policy == expectedPolicy);
    }
}
