using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels;

// The web player's previous and next episode buttons, either side of the skips. While one of them has an episode both
// slots stay, so Play keeps the middle; a movie, or an episode with neither, has no slots.
public sealed partial class PlayerViewModel
{
    private NextEpisode? _previousEpisode;

    [ObservableProperty]
    public partial bool HasPreviousEpisode { get; private set; }

    [ObservableProperty]
    public partial bool ShowsEpisodeSlots { get; private set; }

    /// <summary>The episode before, from where the profile left it, as the web player starts it for the viewer.</summary>
    [RelayCommand]
    private Task PlayPreviousEpisodeAsync() =>
        _previousEpisode is { } previous ? StartAsync(PlayRequest.Resume(previous.ContentId)) : Task.CompletedTask;

    /// <summary>The episode after, as Previous starts its episode.</summary>
    [RelayCommand]
    private Task PlayNextEpisodeAsync() =>
        _nextUp.Next is { } next ? StartAsync(PlayRequest.Resume(next.ContentId)) : Task.CompletedTask;

    partial void OnHasPreviousEpisodeChanged(bool value) => ShowsEpisodeSlots = value || HasNextEpisode;

    partial void OnHasNextEpisodeChanged(bool value) => ShowsEpisodeSlots = HasPreviousEpisode || value;

    private void ResetPreviousEpisode()
    {
        _previousEpisode = null;
        HasPreviousEpisode = false;
    }

    private async Task ResolvePreviousAsync(IUpNextSource source, PlayingEpisode episode, CancellationToken cancellationToken)
    {
        NextEpisode? previous;
        try
        {
            previous = await source.PreviousEpisodeAsync(episode, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        _dispatcher.Post(() =>
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                _previousEpisode = previous;
                HasPreviousEpisode = previous is not null;
            }
        });
    }
}
