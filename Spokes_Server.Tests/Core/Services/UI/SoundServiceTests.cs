using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Moq;
using Spokes_Server.Core.Data;
using Spokes_Server.Core.Data.Repositories.Core;
using Spokes_Server.Core.Data.Repositories.HR;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Services.Core;
using Spokes_Server.Core.Services.UI;
using Xunit;

namespace Spokes_Server.Tests.Core.Services.UI;

public class SoundServiceTests : IDisposable
{
    private readonly string _testDataDir;
    private readonly Mock<IJSRuntime> _mockJs;
    private readonly UserService _userService;
    private readonly SoundService _soundService;

    public SoundServiceTests()
    {
        _testDataDir = Path.Combine(Path.GetTempPath(), "Spokes_Test_SoundService_" + Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDataDir);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "DataPath", _testDataDir } })
            .Build();

        var persistence = new DiskPersistenceService(new Mock<ILogger<DiskPersistenceService>>().Object);
        var employees = new EmployeeRepository(persistence, config);
        var openIds = new OpenIdAccountRepository(persistence, config);

        var mockAuth = new Mock<AuthenticationStateProvider>();
        _userService = new UserService(mockAuth.Object, employees, openIds);

        _mockJs = new Mock<IJSRuntime>();
        _soundService = new SoundService(_mockJs.Object, _userService);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDataDir))
            {
                Directory.Delete(_testDataDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task PlaySoundAsync_WithSystemDefault_DoesNotInvokeJs()
    {
        await _soundService.PlaySoundAsync(NotificationSoundIds.SystemDefault);

        _mockJs.Verify(js => js.InvokeAsync<IJSVoidResult>(
            It.IsAny<string>(),
            It.IsAny<object[]>()), Times.Never);
    }

    [Fact]
    public async Task PlaySoundAsync_WithValidSound_InvokesJsSoundManager()
    {
        await _soundService.PlaySoundAsync("spokesnotif1.wav", isPreview: true);

        _mockJs.Verify(js => js.InvokeAsync<IJSVoidResult>(
            "SoundManager.play",
            It.Is<object[]>(args => (string)args[0] == "spokesnotif1.wav" && (bool)args[1] == true)), Times.Once);
    }

    [Fact]
    public async Task PlayNotificationSoundAsync_WithSystemDefault_DoesNotInvokeJs()
    {
        await _soundService.PlayNotificationSoundAsync(NotificationCategories.Chat, NotificationSoundIds.SystemDefault);

        _mockJs.Verify(js => js.InvokeAsync<IJSVoidResult>(
            It.IsAny<string>(),
            It.IsAny<object[]>()), Times.Never);
    }

    [Fact]
    public async Task PlayNotificationSoundAsync_WithExplicitSound_InvokesJsWithResolvedFile()
    {
        await _soundService.PlayNotificationSoundAsync(
            NotificationCategories.Call,
            "mixkit_vintage_telephone_ringtone_1356",
            isPreview: true);

        _mockJs.Verify(js => js.InvokeAsync<IJSVoidResult>(
            "SoundManager.play",
            It.Is<object[]>(args => (string)args[0] == "mixkit_vintage_telephone_ringtone_1356.wav" && (bool)args[1] == true)), Times.Once);
    }

    [Fact]
    public async Task StopPreviewAsync_InvokesJsStopPreview()
    {
        await _soundService.StopPreviewAsync();

        _mockJs.Verify(js => js.InvokeAsync<IJSVoidResult>(
            "SoundManager.stopPreview",
            It.IsAny<object[]>()), Times.Once);
    }

    [Fact]
    public async Task StopCallRingtoneAsync_InvokesJsStopCallRingtone()
    {
        await _soundService.StopCallRingtoneAsync();

        _mockJs.Verify(js => js.InvokeAsync<IJSVoidResult>(
            "SoundManager.stopCallRingtone",
            It.IsAny<object[]>()), Times.Once);
    }
}
