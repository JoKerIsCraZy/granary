using Granary.Core.Playback;
using Granary.Core.ViewModels.Settings;

namespace Granary.Tests.Settings;

/// <summary>Settings → Playback → This PC, below "mpv configuration": what Granary's editor changed.</summary>
public sealed class MpvSettingsViewModelTests
{
    private readonly MpvConfigChanges _changes = new();

    [Fact]
    public void ASave_IsSaid_ToApplyAfterARestart()
    {
        using var row = new MpvSettingsViewModel(_changes);
        Assert.Equal("", row.ChangeText);

        _changes.Saved();

        Assert.Equal("Your changes to mpv.conf apply after you restart Granary.", row.ChangeText);
    }

    [Fact]
    public void ASaveBeforeThePageOpened_IsSaidToo()
    {
        _changes.Saved();

        using var row = new MpvSettingsViewModel(_changes);

        Assert.Equal(MpvSettingsViewModel.PendingText, row.ChangeText);
    }

    [Fact]
    public void APageThatLeft_StopsFollowing()
    {
        var row = new MpvSettingsViewModel(_changes);

        row.Dispose();
        _changes.Saved();

        Assert.Equal("", row.ChangeText);
    }
}
