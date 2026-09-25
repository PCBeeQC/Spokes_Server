using System.Text.Json;
using Spokes_Server.Core.Models.Communication.Notifications;
using Spokes_Server.Core.Models.HR;
using Xunit;

namespace Spokes_Server.Tests.Core.Models.HR;

public class EmployeeSoundPreferencesTests
{
    [Fact]
    public void GetNotificationSound_DefaultsToSpokesDefault_WhenNotSet()
    {
        var employee = new Employee();

        var chatSound = employee.GetNotificationSound(NotificationCategories.Chat);
        var callSound = employee.GetNotificationSound(NotificationCategories.Call);
        var calSound = employee.GetNotificationSound(NotificationCategories.Calendar);

        Assert.Equal(NotificationSoundIds.SpokesDefault, chatSound);
        Assert.Equal(NotificationSoundIds.SpokesDefault, callSound);
        Assert.Equal(NotificationSoundIds.SpokesDefault, calSound);
    }

    [Fact]
    public void SetNotificationSound_StoresAndRetrievesSound()
    {
        var employee = new Employee();

        employee.SetNotificationSound(NotificationCategories.Chat, "mixkit_cowbell_sharp_hit_1743");
        employee.SetNotificationSound(NotificationCategories.Call, NotificationSoundIds.SystemDefault);

        Assert.Equal("mixkit_cowbell_sharp_hit_1743", employee.GetNotificationSound(NotificationCategories.Chat));
        Assert.Equal(NotificationSoundIds.SystemDefault, employee.GetNotificationSound(NotificationCategories.Call));
        Assert.Equal(NotificationSoundIds.SpokesDefault, employee.GetNotificationSound(NotificationCategories.Calendar));
    }

    [Fact]
    public void NotificationSounds_SurvivesJsonSerialization()
    {
        var employee = new Employee
        {
            Id = "emp-1",
            FirstName = "Alice",
            LastName = "Smith"
        };
        employee.SetNotificationSound(NotificationCategories.Chat, "mixkit_happy_bell_alert_601");
        employee.SetNotificationSound(NotificationCategories.Call, "mixkit_on_hold_ringtone_1361");

        var json = JsonSerializer.Serialize(employee);
        var deserialized = JsonSerializer.Deserialize<Employee>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("mixkit_happy_bell_alert_601", deserialized.GetNotificationSound(NotificationCategories.Chat));
        Assert.Equal("mixkit_on_hold_ringtone_1361", deserialized.GetNotificationSound(NotificationCategories.Call));
    }
}
