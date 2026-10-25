using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api.Settings;
using Granary.Core.Playback;
using Granary.Core.Settings;
using Granary.Core.ViewModels.Settings;
using Microsoft.Extensions.Logging;

namespace Granary.Core.ViewModels;

// The web player's Subtitle appearance panel (SubtitleAppearancePanel): the look changed in the player shows at once
// and is saved as the profile's as it changes, where the settings page keeps it too; "Use fallback style" clears the
// profile's and this device's look, so the server's default applies again.
public sealed partial class PlayerViewModel
{
    public const string AppearanceSavedText = "Changes saved automatically";

    private readonly IProfileSettings? _settings;
    private SettingSaves? _lookSaves;

    // The look the viewer set here, until a new title reads the profile's; the latest one waiting to be saved; whether
    // this device keeps a look of its own, which a save clears.
    private SubtitleAppearance? _look;
    private SubtitleAppearance? _lookToSave;
    private bool _lookOnDevice;

    // Bumped by each opening and change: a read that comes back after one does not put an older look up.
    private int _lookGeneration;

    /// <summary>The look in the panel.</summary>
    public SubtitleAppearanceEditor Look { get; } = new();

    [ObservableProperty]
    public partial bool IsAppearanceOpen { get; private set; }

    /// <summary>The footer's line: saved automatically, or what went wrong.</summary>
    [ObservableProperty]
    public partial string AppearanceStatus { get; private set; } = AppearanceSavedText;

    /// <summary>Opens the panel on the look that shows, then on the profile's as the server has it now.</summary>
    [RelayCommand]
    private void OpenAppearance()
    {
        Look.Show(_look ?? _playback?.Preferences.Subtitles ?? SubtitleAppearanceEditor.WebDefault);
        if (_look is null)
        {
            // Until the read below says otherwise: a look this device keeps would outrank what is saved here.
            _lookOnDevice = _playback?.Preferences.SubtitlesOnDevice ?? false;
        }
        AppearanceStatus = AppearanceSavedText;
        IsAppearanceOpen = true;
        _ = ReadLookAsync(++_lookGeneration);
    }

    [RelayCommand]
    private void CloseAppearance() => IsAppearanceOpen = false;

    /// <summary>"Use fallback style": the stored looks are cleared, after the changes before; the server's default shows.</summary>
    [RelayCommand]
    private Task ResetAppearanceAsync()
    {
        if (_settings is not { } settings)
        {
            return Task.CompletedTask;
        }
        _lookToSave = null;
        int generation = ++_lookGeneration;
        SubtitleAppearance fallback = SubtitleAppearanceEditor.WebDefault;
        return LookSaves.RunAsync(async () =>
        {
            await settings.ResetAsync(SettingKeys.SubtitleAppearance, CancellationToken.None);
            _lookOnDevice = false;
            fallback = await ReadEffectiveLookAsync(settings) ?? SubtitleAppearanceEditor.WebDefault;
        }, reset =>
        {
            if (!reset)
            {
                AppearanceStatus = "Couldn't reset the look. Try again.";
                return;
            }
            // A change made meanwhile stays, and is saved after this.
            if (generation == _lookGeneration)
            {
                ShowLook(fallback);
                AppearanceStatus = AppearanceSavedText;
            }
        });
    }

    private SettingSaves LookSaves => _lookSaves ??= new SettingSaves(_logger);

    // The viewer changed a field: it shows at once; saved after the saves before it, the latest change only.
    private void OnLookEdited(object? sender, EventArgs e)
    {
        var look = Look.Appearance;
        _lookGeneration++;
        _look = look;
        _engine.SetSubtitleLook(look);
        if (_settings is not { } settings)
        {
            return;
        }
        _lookToSave = look;
        _ = LookSaves.RunAsync(async () =>
        {
            if (!ReferenceEquals(_lookToSave, look))
            {
                return;
            }
            bool clearDevice = _lookOnDevice;
            await settings.SaveAsync(SettingKeys.SubtitleAppearance, look.ToJson(), clearDevice, CancellationToken.None);
            _lookOnDevice &= !clearDevice;
        }, saved =>
        {
            if (ReferenceEquals(_lookToSave, look))
            {
                _lookToSave = null;
                // Best effort, as on the web: the look stays on screen, and the next change saves it with the rest.
                AppearanceStatus = saved ? AppearanceSavedText : "Couldn't save. The next change tries again.";
            }
        });
    }

    private async Task ReadLookAsync(int generation)
    {
        if (_settings is not { } settings)
        {
            return;
        }
        try
        {
            var values = await settings.ReadAsync([SettingKeys.SubtitleAppearance], CancellationToken.None);
            if (generation != _lookGeneration || !values.TryGetValue(SettingKeys.SubtitleAppearance, out var value))
            {
                return;
            }
            _lookOnDevice = value.Source == SettingScopes.ProfileDevice;
            ShowLook(SubtitleAppearance.From(value.Value));
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex) || ex is InvalidOperationException)
        {
            _logger.LogInformation("The subtitle look could not be read: {Failure}", ApiFailureText.Describe(ex));
        }
    }

    private async Task<SubtitleAppearance?> ReadEffectiveLookAsync(IProfileSettings settings)
    {
        try
        {
            var values = await settings.ReadAsync([SettingKeys.SubtitleAppearance], CancellationToken.None);
            return values.TryGetValue(SettingKeys.SubtitleAppearance, out var value) ? SubtitleAppearance.From(value.Value) : null;
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex))
        {
            _logger.LogInformation("The default subtitle look could not be read: {Failure}", ApiFailureText.Describe(ex));
            return null;
        }
    }

    private void ShowLook(SubtitleAppearance look)
    {
        Look.Show(look);
        if (look != (_look ?? _playback?.Preferences.Subtitles))
        {
            _engine.SetSubtitleLook(look);
        }
        _look = look;
    }

    // A new title shows the profile's look as it reads it.
    private void ResetLook()
    {
        _look = null;
        _lookOnDevice = false;
        IsAppearanceOpen = false;
    }
}
