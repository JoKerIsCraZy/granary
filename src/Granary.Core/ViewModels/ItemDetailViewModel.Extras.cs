using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.ViewModels.Detail;

namespace Granary.Core.ViewModels;

// The title's local extras, the web's ExtrasSection.tsx: featurettes, deleted scenes and the like in groups by kind. Each
// plays in Granary's player from its start, as the Android and Apple apps play an extra: nothing offers to resume one,
// and as it is no episode nothing plays next after it.
public sealed partial class ItemDetailViewModel
{
    /// <summary>The Extras section's groups; empty when the server lists none, and the section hides.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExtras))]
    public partial IReadOnlyList<DetailExtraGroup> ExtraGroups { get; private set; } = [];

    public bool HasExtras => ExtraGroups.Count > 0;

    [RelayCommand]
    private void PlayExtra(DetailExtra? extra)
    {
        if (extra is not null)
        {
            _navigation.NavigateTo(AppRoute.Player, PlayRequest.FromStart(extra.ContentId));
        }
    }
}
