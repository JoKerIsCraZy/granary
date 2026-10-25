using Granary.Core.Api.Playback;

namespace Granary.Tests.Playback;

/// <summary>The audio pick as the server keeps it (docs/api-contract.md, 8.8).</summary>
public sealed class TrackPreferencesTests
{
    [Fact]
    public void APick_CarriesItsOrdinalLanguageAndSignature()
    {
        var track = new WatchAudioTrack
        {
            Language = " de ", Title = "Deutsch", EmbeddedTitle = "", Codec = "eac3", Layout = "5.1(side)", Channels = 6, Default = true,
        };

        var preference = AudioPreference.Of(track, 2);

        Assert.Equal(2, preference.AudioTrackIndex);
        Assert.Equal("de", preference.AudioLanguage);
        Assert.Equal(new AudioTrackSignature { Language = "de", Title = "Deutsch", Codec = "eac3", Layout = "5.1(side)", Channels = 6 },
            preference.TrackSignature);
    }

    [Fact]
    public void ATrackWithNothingToMatch_HasNoSignature() =>
        Assert.Null(AudioPreference.Of(new WatchAudioTrack(), 0).TrackSignature);

    [Theory]
    [InlineData("de", "de")]
    [InlineData("ger", "ger")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("zh_Hant", "zh_Hant")]
    [InlineData("und", null)]
    [InlineData("mul", null)]
    [InlineData("zxx", null)]
    [InlineData("English", null)]
    [InlineData("Português", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyATagTheServerTakes_IsSentAsTheLanguage(string? language, string? expected) =>
        Assert.Equal(expected, AudioPreference.LanguageTag(language));

    [Fact]
    public void AnEpisodesPick_IsKeptForItsSeries_AMoviesForItself()
    {
        var episode = new WatchDetail { ContentId = "episode-1", Type = WatchItemTypes.Episode, SeriesId = "series-9" };
        var movie = new WatchDetail { ContentId = "movie-1", Type = WatchItemTypes.Movie, SeriesId = "series-9" };

        Assert.Equal("series-9", TrackPreferencesApi.KeyFor(episode));
        Assert.Equal("movie-1", TrackPreferencesApi.KeyFor(movie));
        Assert.Equal("episode-1", TrackPreferencesApi.KeyFor(episode with { SeriesId = null }));
    }
}
