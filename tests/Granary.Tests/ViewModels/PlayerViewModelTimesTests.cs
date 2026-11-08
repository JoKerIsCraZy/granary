using Granary.Core.Playback;
using Granary.Core.ViewModels;
using Granary.Tests.Fakes;

namespace Granary.Tests.ViewModels;

/// <summary>The times under the player's title: the duration or the time left, as this PC chose last.</summary>
public sealed class PlayerViewModelTimesTests
{
    private readonly FakePlaybackEngine _engine = new();
    private readonly ManualTimeProvider _time = new();
    private readonly InMemoryPlaybackDevicePreferences _preferences = new();

    private PlayerViewModel Create(IPlaybackDevicePreferences? preferences) =>
        new(_engine, new ImmediateDispatcher(), new RecordingNavigation(), () => new FakeServerPlayback(), new PlaybackStops(), _time,
            devicePreferences: preferences);

    [Fact]
    public void AtFirst_TheTimeShowsTheDuration()
    {
        using var viewModel = Create(_preferences);

        Assert.False(viewModel.ShowsTimeLeft);
    }

    [Fact]
    public void ANewPlayer_ShowsTheTimeLeft_WhenThisPcChoseItLast()
    {
        _preferences.TimeLeft = true;

        using var viewModel = Create(_preferences);

        Assert.True(viewModel.ShowsTimeLeft);
        Assert.Empty(_preferences.SavedTimeLeft);
    }

    [Fact]
    public void AClick_SwitchesBetweenTheDurationAndTheTimeLeft_AndThisPcKeepsEachChoice()
    {
        using var viewModel = Create(_preferences);

        viewModel.ToggleTimeLeftCommand.Execute(null);
        Assert.True(viewModel.ShowsTimeLeft);
        viewModel.ToggleTimeLeftCommand.Execute(null);
        Assert.False(viewModel.ShowsTimeLeft);

        Assert.Equal([true, false], _preferences.SavedTimeLeft);
    }

    [Fact]
    public void WithoutThisPcsChoices_AClickStillSwitches()
    {
        using var viewModel = Create(null);

        viewModel.ToggleTimeLeftCommand.Execute(null);

        Assert.True(viewModel.ShowsTimeLeft);
    }
}
