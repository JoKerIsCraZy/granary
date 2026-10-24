using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api;
using Granary.Core.Api.Profiles;
using Granary.Core.Auth;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

/// <summary>Home. For now only who is signed in where; the catalog rows follow in Phase 3b.</summary>
public sealed partial class HomeViewModel(ISessionManager session, ProfilesApi profiles, ILogger<HomeViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    public partial string ServerName { get; private set; } = "";

    [ObservableProperty]
    public partial string UserName { get; private set; } = "";

    [ObservableProperty]
    public partial string ProfileName { get; private set; } = "";

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ServerName = session.ActiveServer?.Name ?? "";
        UserName = session.Account?.Username ?? "";
        if (session.ActiveServer is not { } server || session.ProfileId is not { } profileId)
        {
            return;
        }
        try
        {
            var collection = await profiles.ListAsync(server.BaseUri, cancellationToken);
            ProfileName = collection.Items.FirstOrDefault(p => p.Id == profileId)?.Name ?? "";
        }
        catch (Exception ex) when (ex is HttpRequestException or SiloApiException)
        {
            logger.LogWarning(ex, "Could not load the profile name");
        }
    }

    [RelayCommand]
    private Task SwitchProfileAsync(CancellationToken cancellationToken) => session.ClearProfileAsync(cancellationToken);

    [RelayCommand]
    private Task SignOutAsync(CancellationToken cancellationToken) => session.SignOutAsync(cancellationToken: cancellationToken);
}
