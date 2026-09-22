using PersonalDesktopHelper.Copilot;
using PersonalDesktopHelper.Persistence;

namespace PersonalDesktopHelper.Tests;

public sealed class CopilotSettingsTests
{
    [Fact]
    public void ExistingProfilesLoadWithDefaultCopilotSettings()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.StatePath, """{"version":1,"notificationsEnabled":false,"tasks":[]}""");

        var store = new JsonStateStore(directory.StatePath);

        Assert.False(store.State.NotificationsEnabled);
        Assert.Equal(new CopilotSettings(), store.State.Copilot);
    }

    [Fact]
    public void CopilotSettingsRoundTripWithoutChangingOtherOptions()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonStateStore(directory.StatePath);
        store.SetNotificationsEnabled(false);
        var settings = new CopilotSettings { Model = "test-model" };

        store.SetCopilotSettings(settings);

        var loaded = new JsonStateStore(directory.StatePath);
        Assert.Equal(settings, loaded.State.Copilot);
        Assert.False(loaded.State.NotificationsEnabled);
        Assert.Empty(loaded.State.Tasks);
    }

    [Fact]
    public void InvalidSettingsDoNotReplaceTheSavedProfile()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonStateStore(directory.StatePath);
        store.SetNotificationsEnabled(false);
        var before = File.ReadAllText(directory.StatePath);

        Assert.Throws<InvalidDataException>(() =>
            store.SetCopilotSettings(new CopilotSettings { Model = "invalid\nmodel" }));

        Assert.Equal(before, File.ReadAllText(directory.StatePath));
        Assert.Equal(new CopilotSettings(), store.State.Copilot);
    }
}
