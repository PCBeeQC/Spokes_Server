using Moq;
using Spokes_Server.Core.Services.Communication.Chat;
using Spokes_Server.Core.Services.Communication.Presence;

namespace Spokes_Server.Tests.Core.Services.Communication.Presence;

public class PresenceCircuitHandlerTests : IDisposable
{
    private readonly ChatStateService _chatState;
    private readonly PresenceStateService _presenceState;
    private readonly UserCircuitContext _circuitContext;
    private readonly Mock<UserClientStateService> _mockClientStateService;
    private readonly PresenceCircuitHandler _handler;

    public PresenceCircuitHandlerTests()
    {
        _chatState = new ChatStateService();
        _presenceState = new PresenceStateService(_chatState);
        _circuitContext = new UserCircuitContext();
        _mockClientStateService = new Mock<UserClientStateService>();
        _handler = new PresenceCircuitHandler(_presenceState, _circuitContext, _mockClientStateService.Object);
    }

    [Fact]
    public async Task OnConnectionUpAsync_WithUserIdAndSubscriptionId_RegistersPresence()
    {
        // Arrange
        _circuitContext.UserId = "user-123";
        _circuitContext.SubscriptionId = "sub-123";
        
        // Act
        await _handler.OnConnectionUpAsync(null!, CancellationToken.None);

        // Assert
        Assert.True(_presenceState.HasActiveConnection("user-123"));
    }

    [Fact]
    public async Task OnConnectionUpAsync_WithUserIdAndNoSubscription_ThrowsNullReferenceBecauseCircuitCannotBeMocked()
    {
        // Arrange
        _circuitContext.UserId = "user-456";
        _circuitContext.SubscriptionId = null;
        
        // Act & Assert
        // We cannot mock Circuit, so passing null causes NRE when it falls back to circuit.Id
        await Assert.ThrowsAsync<NullReferenceException>(() => 
            _handler.OnConnectionUpAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task OnConnectionUpAsync_WithoutUserId_DoesNotRegisterPresence()
    {
        // Arrange
        _circuitContext.UserId = null;
        _circuitContext.SubscriptionId = "sub-123";
        
        // Act
        await _handler.OnConnectionUpAsync(null!, CancellationToken.None);

        // Assert
        Assert.False(_presenceState.HasActiveConnection("user-123"));
    }

    [Fact]
    public async Task OnConnectionDownAsync_WithSubscriptionId_RemovesConnection()
    {
        // Arrange
        _circuitContext.UserId = "user-789";
        _circuitContext.SubscriptionId = "sub-789";
        
        // Manually register first
        _presenceState.RegisterConnection("user-789", "sub-789", "Desktop");
        Assert.True(_presenceState.HasActiveConnection("user-789"));

        // Act
        await _handler.OnConnectionDownAsync(null!, CancellationToken.None);

        // Assert
        Assert.False(_presenceState.HasActiveConnection("user-789"));
        _mockClientStateService.Verify(s => s.FlushToDiskIfUnsent("user-789"), Times.Once);
    }

    [Fact]
    public async Task OnConnectionDownAsync_WithoutSubscriptionId_ThrowsNullReferenceBecauseCircuitCannotBeMocked()
    {
        // Arrange
        _circuitContext.UserId = "user-101";
        _circuitContext.SubscriptionId = null;
        
        // Act & Assert
        await Assert.ThrowsAsync<NullReferenceException>(() => 
            _handler.OnConnectionDownAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task OnConnectionDownAsync_MarksCircuitContextDisconnectedAndUnfocused()
    {
        // Arrange
        _circuitContext.UserId = "user-down";
        _circuitContext.SubscriptionId = "sub-down";
        _circuitContext.IsConnected = true;
        _circuitContext.IsFocused = true;

        // Act
        await _handler.OnConnectionDownAsync(null!, CancellationToken.None);

        // Assert
        Assert.False(_circuitContext.IsConnected);
        Assert.False(_circuitContext.IsFocused);
        Assert.False(_circuitContext.IsActivelyViewed);
    }

    [Fact]
    public async Task OnConnectionUpAsync_MarksCircuitContextConnectedAndUnfocusedUntilClientReportsFocus()
    {
        // Arrange
        _circuitContext.UserId = "user-up";
        _circuitContext.SubscriptionId = "sub-up";
        _circuitContext.DeviceType = "Mobile";
        _circuitContext.IsConnected = false;
        _circuitContext.IsFocused = true;

        // Act
        await _handler.OnConnectionUpAsync(null!, CancellationToken.None);

        // Assert
        Assert.True(_circuitContext.IsConnected);
        Assert.False(_circuitContext.IsFocused);
        Assert.False(_circuitContext.IsActivelyViewed);
        Assert.False(_presenceState.IsConnectionActivelyViewed("sub-up"));
    }

    [Fact]
    public void ShouldDisplayInAppNotification_MobileUnfocused_ReturnsFalse()
    {
        _circuitContext.DeviceType = "Mobile";
        _circuitContext.SubscriptionId = "sub-mob-test";
        _circuitContext.IsConnected = true;
        _circuitContext.IsFocused = false;
        _presenceState.RegisterConnection("u1", "sub-mob-test", "Mobile", isFocused: false);

        Assert.False(_circuitContext.ShouldDisplayInAppNotification(_presenceState));
    }

    [Fact]
    public void ShouldDisplayInAppNotification_MobileFocusedAndViewed_ReturnsTrue()
    {
        _circuitContext.DeviceType = "Mobile";
        _circuitContext.SubscriptionId = "sub-mob-test";
        _circuitContext.IsConnected = true;
        _circuitContext.IsFocused = true;
        _presenceState.RegisterConnection("u1", "sub-mob-test", "Mobile", isFocused: true);

        Assert.True(_circuitContext.ShouldDisplayInAppNotification(_presenceState));
    }

    [Fact]
    public void ShouldDisplayInAppNotification_Desktop_ReturnsTrue()
    {
        _circuitContext.DeviceType = "Desktop";
        _circuitContext.IsConnected = true;
        _circuitContext.IsFocused = false;

        Assert.True(_circuitContext.ShouldDisplayInAppNotification(_presenceState));
    }

    [Fact]
    public void ShouldDisplayInAppNotification_DesktopWithPushEnabledUnfocused_ReturnsFalse()
    {
        _circuitContext.DeviceType = "Desktop";
        _circuitContext.HasPushEnabled = true;
        _circuitContext.IsConnected = true;
        _circuitContext.IsFocused = false;

        Assert.False(_circuitContext.ShouldDisplayInAppNotification(_presenceState));
    }

    [Fact]
    public void ShouldDisplayInAppNotification_DesktopWithPushEnabledFocused_ReturnsTrue()
    {
        _circuitContext.DeviceType = "Desktop";
        _circuitContext.HasPushEnabled = true;
        _circuitContext.IsConnected = true;
        _circuitContext.IsFocused = true;

        Assert.True(_circuitContext.ShouldDisplayInAppNotification(_presenceState));
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}

