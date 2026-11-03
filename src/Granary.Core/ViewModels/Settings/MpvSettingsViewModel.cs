using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Abstractions;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Playback → This PC, below "mpv configuration": what Granary's own editor changed, and the presets, named
/// copies of mpv.conf (Granary's own; Silo's clients have no player configuration). Loading a preset is a change like a
/// save. The page disposes it as it leaves.
/// </summary>
public sealed partial class MpvSettingsViewModel : ObservableObject, IDisposable
{
    public const string PendingText = "Your changes to mpv.conf apply after you restart Granary, or with Apply now.";

    public const string NextTitleText = "Applies to the next title.";

    private const string DeviceNameText = "Windows keeps this name for itself. Choose another.";

    private readonly MpvConfigFolder _folder;
    private readonly MpvPresets _presets;
    private readonly MpvConfigChanges _changes;
    private readonly IConfirmation _confirmation;

    // One preset action at a time: each asks first, and a second press while it asks does nothing.
    private bool _busy;

    public MpvSettingsViewModel(MpvConfigFolder folder, MpvPresets presets, MpvConfigChanges changes, IConfirmation confirmation)
    {
        _folder = folder;
        _presets = presets;
        _changes = changes;
        _confirmation = confirmation;
        _changes.Changed += OnChanged;
        ShowChanges();
    }

    /// <summary>That mpv.conf changed since the player read it, or that it applies to the next title; empty otherwise.</summary>
    [ObservableProperty]
    public partial string ChangeText { get; private set; } = "";

    /// <summary>The player runs with the file as it was: Apply now starts it again.</summary>
    [ObservableProperty]
    public partial bool ShowsApplyNow { get; private set; }

    /// <summary>Starts the player's mpv again with mpv.conf as it is now, or once the title that plays ends.</summary>
    [RelayCommand]
    private Task ApplyNowAsync() => _changes.ApplyNowAsync();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPresets))]
    public partial IReadOnlyList<MpvPresetRow> Presets { get; private set; } = [];

    public bool HasPresets => Presets.Count > 0;

    /// <summary>How the last preset action went; empty before one.</summary>
    [ObservableProperty]
    public partial string PresetMessage { get; private set; } = "";

    /// <summary>"Preset: HQ", "(changed)" after mpv.conf was edited since; empty while no preset is loaded.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivePreset))]
    public partial string ActivePresetText { get; private set; } = "";

    /// <summary>A preset is loaded: Unload shows.</summary>
    public bool HasActivePreset => ActivePresetText.Length > 0;

    /// <summary>What the name field says of a typed name: the name it is saved under when that differs, or why it cannot be.</summary>
    public static string PresetNameNote(string typed)
    {
        string clean = MpvPresets.Clean(typed);
        return clean.Length == 0 ? "Enter a name."
            : MpvPresets.IsDeviceName(clean) ? DeviceNameText
            : clean == typed.Trim() ? ""
            : $"Saves as {clean}.";
    }

    public static bool CanSavePresetAs(string typed) => MpvPresets.SafeName(typed) is not null;

    /// <summary>Reads the presets and the one in use.</summary>
    public async Task LoadPresetsAsync()
    {
        Presets = await Task.Run(ReadPresets);
        await ShowActiveAsync();
    }

    /// <summary>Keeps the mpv.conf on disk as a preset under the typed name, made safe; one of that name is replaced only after asking.</summary>
    public Task SavePresetAsync(string typed) => RunAsync(async () =>
    {
        if (MpvPresets.SafeName(typed) is not { } name)
        {
            PresetMessage = PresetNameNote(typed);
            return;
        }
        var content = await Task.Run(_folder.ReadConfig);
        if (content is null || content.IsNew)
        {
            PresetMessage = content is null ? "Couldn't read your mpv.conf." : "There's no mpv.conf to save yet.";
            return;
        }
        if (await Task.Run(() => _presets.Exists(name))
            && !await _confirmation.AskAsync($"Replace the preset {name}?",
                "A preset of that name is already saved. Saving replaces it with your mpv.conf.", "Replace"))
        {
            return;
        }
        bool saved = await Task.Run(() => _presets.Save(name, content.Text));
        PresetMessage = saved ? $"Saved your mpv.conf as {name}." : $"Couldn't save the preset {name}.";
        Presets = await Task.Run(ReadPresets);
    });

    /// <summary>Replaces mpv.conf with the preset, after asking: a change the player takes when it starts again.</summary>
    public Task LoadPresetAsync(MpvPresetRow preset) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!await _confirmation.AskAsync($"Load the preset {preset.Name}?",
                "It replaces your mpv.conf. Save mpv.conf as a preset first to keep it.", "Load"))
        {
            return;
        }
        if (!await Task.Run(() => _presets.Load(preset.Name)))
        {
            PresetMessage = $"Couldn't load the preset {preset.Name}.";
            Presets = await Task.Run(ReadPresets);
            return;
        }
        PresetMessage = $"Loaded {preset.Name} into mpv.conf.";
        // Through the change: the row reads the preset in use again.
        _changes.Saved();
        await ShowActiveAsync();
    });

    /// <summary>Goes back to mpv.conf from before the first preset, after asking: a change like a save.</summary>
    public Task UnloadPresetAsync() => RunAsync(async () =>
    {
        if (!await _confirmation.AskAsync("Go back to your mpv.conf from before you loaded a preset?", "Changes made since are lost.", "Unload"))
        {
            return;
        }
        var result = await Task.Run(_presets.Unload);
        PresetMessage = result switch
        {
            MpvPresetUnload.Restored => "Your mpv.conf from before the preset is back.",
            MpvPresetUnload.Removed => "You had no mpv.conf before the preset, so it is removed.",
            MpvPresetUnload.NothingToRestore => "Couldn't find your mpv.conf from before the preset.",
            _ => "Couldn't go back to your mpv.conf from before the preset.",
        };
        if (result is MpvPresetUnload.Restored or MpvPresetUnload.Removed)
        {
            _changes.Saved();
        }
        await ShowActiveAsync();
    });

    public Task DeletePresetAsync(MpvPresetRow preset) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!await _confirmation.AskAsync($"Delete the preset {preset.Name}?", "Its file goes from the presets folder. This can't be undone.",
                "Delete"))
        {
            return;
        }
        bool deleted = await Task.Run(() => _presets.Delete(preset.Name));
        PresetMessage = deleted ? $"Deleted the preset {preset.Name}." : $"Couldn't delete the preset {preset.Name}.";
        Presets = await Task.Run(ReadPresets);
    });

    public void Dispose() => _changes.Changed -= OnChanged;

    private async Task RunAsync(Func<Task> action)
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        PresetMessage = "";
        try
        {
            await action();
        }
        finally
        {
            _busy = false;
        }
    }

    private IReadOnlyList<MpvPresetRow> ReadPresets() => [.. _presets.Names().Select(name => new MpvPresetRow(name))];

    private async Task ShowActiveAsync()
    {
        var active = await Task.Run(_presets.Active);
        ActivePresetText = active is null ? "" : active.IsChanged ? $"Preset: {active.Name} (changed)" : $"Preset: {active.Name}";
    }

    // An edit in Granary's editor can make the preset in use "changed". Active() logs a disk that fails and answers null.
    private void OnChanged(object? sender, EventArgs e)
    {
        ShowChanges();
        _ = ShowActiveAsync();
    }

    private void ShowChanges()
    {
        ChangeText = _changes.State switch
        {
            MpvConfigApply.Pending => PendingText,
            MpvConfigApply.NextTitle => NextTitleText,
            _ => "",
        };
        ShowsApplyNow = _changes.State == MpvConfigApply.Pending;
    }
}

/// <summary>A preset in the list, with what its buttons are called for a screen reader.</summary>
public sealed record MpvPresetRow(string Name)
{
    public string LoadName => $"Load the preset {Name}";

    public string DeleteName => $"Delete the preset {Name}";
}
