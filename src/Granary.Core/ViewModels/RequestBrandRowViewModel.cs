using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Api.Requests;

namespace Granary.Core.ViewModels;

/// <summary>A studio, network or genre to browse, as the web's <c>BrandCard</c>: a logo, the name, or for a genre the name on a gradient.</summary>
/// <param name="Logo">A studio's or network's logo; null shows the name.</param>
/// <param name="GradientFrom">A genre's colours, "#RRGGBB" from the top left to the bottom right.</param>
/// <param name="Destination">Its titles; a genre opens on movies.</param>
/// <param name="OpenCommand">Takes the tile.</param>
public sealed record RequestBrandTile(string Name, Uri? Logo, string GradientFrom, string GradientTo, bool IsGenre,
    RequestTitlesRouteParameter Destination, ICommand OpenCommand)
{
    public bool HasLogo => Logo is not null;
}

/// <summary>
/// A row of the Requests hub's Discover tab, as the web's <c>BrandCarousel</c>: Studios, Networks or Genres, loaded on
/// their own, with their own failure and Retry. A row without any stays hidden.
/// </summary>
public sealed partial class RequestBrandRowViewModel : ObservableObject
{
    internal RequestBrandRowViewModel(DiscoverBrowseKind kind, string title, Func<RequestBrandRowViewModel, Task> retry)
    {
        Kind = kind;
        Title = title;
        RetryCommand = new AsyncRelayCommand(() => retry(this));
    }

    public DiscoverBrowseKind Kind { get; }

    public string Title { get; }

    /// <summary>"Could not load studios."</summary>
    public string FailureText => $"Could not load {Title.ToLowerInvariant()}.";

    public ObservableCollection<RequestBrandTile> Tiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(HasError), nameof(IsReady), nameof(IsShown))]
    public partial LibraryLoadState State { get; internal set; }

    public bool IsLoading => State == LibraryLoadState.Loading;

    public bool HasError => State == LibraryLoadState.Error;

    public bool IsReady => State == LibraryLoadState.Ready;

    public bool IsShown => State is LibraryLoadState.Loading or LibraryLoadState.Ready or LibraryLoadState.Error;

    public IAsyncRelayCommand RetryCommand { get; }
}

/// <summary>The tiles of the Discover tab's Studios, Networks and Genres.</summary>
internal static partial class RequestBrandTiles
{
    // The web's colours for a genre without its own.
    private const string DefaultFrom = "#475569";
    private const string DefaultTo = "#0f172a";

    /// <summary>The tile; null for a brand without a slug, which has nowhere to go.</summary>
    public static RequestBrandTile? Of(DiscoverBrowseKind kind, DiscoverBrand brand, ICommand openCommand)
    {
        if (string.IsNullOrWhiteSpace(brand.Slug))
        {
            return null;
        }
        bool genre = kind == DiscoverBrowseKind.Genre;
        string name = brand.DisplayName.Length > 0 ? brand.DisplayName : brand.Slug;
        return new RequestBrandTile(name, genre ? null : TmdbImages.Logo(brand.LogoUrl), Colour(brand.GradientFrom, DefaultFrom),
            Colour(brand.GradientTo, DefaultTo), genre, new RequestTitlesRouteParameter(brand.Slug, name, kind, genre ? "movie" : null), openCommand);
    }

    private static string Colour(string? value, string fallback) => value is not null && HexColour().IsMatch(value) ? value : fallback;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColour();
}
