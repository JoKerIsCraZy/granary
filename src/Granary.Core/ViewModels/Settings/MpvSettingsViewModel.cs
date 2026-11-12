using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Playback;
using Granary.Core.Texts;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Playback → This PC, below "mpv configuration": what Granary's own editor changed, and the presets, named
/// copies of mpv.conf (Granary's own; Silo's clients have no player configuration). Loading a preset is a change like a
/// save. The page disposes it as it leaves.
/// </summary>
public sealed partial class MpvSettingsViewModel : ObservableObject, IDisposable
{
    public static string NextTitleText => AppText.Get("granary.settings.mpv.nextTitle");

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

    /// <summary>That a change made here applies to the next title, until a title has started with it; empty otherwise.</summary>
    [ObservableProperty]
    public partial string ChangeText { get; private set; } = "";

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
        return clean.Length == 0 ? AppText.Get("granary.settings.mpv.presetName.empty")
            : MpvPresets.IsDeviceName(clean) ? AppText.Get("granary.settings.mpv.presetName.reserved")
            : clean == typed.Trim() ? ""
            : AppText.Get("granary.settings.mpv.presetName.savesAs", ("name", clean));
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
        if (content is null || content.IsNew || content.IsNotText)
        {
            PresetMessage = content is null ? AppText.Get("granary.settings.mpv.preset.readFailed")
                : content.IsNew ? AppText.Get("granary.settings.mpv.preset.nothingToSave")
                : AppText.Get("granary.settings.mpv.preset.notText");
            return;
        }
        if (await Task.Run(() => _presets.Exists(name))
            && !await _confirmation.AskAsync(AppText.Get("granary.settings.mpv.preset.replaceTitle", ("name", name)),
                AppText.Get("granary.settings.mpv.preset.replaceBody"), AppText.Get("granary.settings.mpv.preset.replace")))
        {
            return;
        }
        bool saved = await Task.Run(() => _presets.Save(name, content.Text));
        PresetMessage = saved
            ? AppText.Get("granary.settings.mpv.preset.saved", ("name", name))
            : AppText.Get("granary.settings.mpv.preset.saveFailed", ("name", name));
        Presets = await Task.Run(ReadPresets);
    });

    /// <summary>Replaces mpv.conf with the preset, after asking: a change the player takes with the next title.</summary>
    public Task LoadPresetAsync(MpvPresetRow preset) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!await _confirmation.AskAsync(AppText.Get("granary.settings.mpv.preset.loadTitle", ("name", preset.Name)),
                AppText.Get("granary.settings.mpv.preset.loadBody"), AppText.Get("granary.settings.mpv.load")))
        {
            return;
        }
        if (!await Task.Run(() => _presets.Load(preset.Name)))
        {
            PresetMessage = AppText.Get("granary.settings.mpv.preset.loadFailed", ("name", preset.Name));
            Presets = await Task.Run(ReadPresets);
            // One that could not take the preset's name back leaves it named, with Unload as the way out.
            await ShowActiveAsync();
            return;
        }
        PresetMessage = AppText.Get("granary.settings.mpv.preset.loaded", ("name", preset.Name));
        // Through the change: the row reads the preset in use again.
        _changes.Saved();
        await ShowActiveAsync();
    });

    /// <summary>Goes back to mpv.conf from before the first preset, after asking: a change like a save.</summary>
    public Task UnloadPresetAsync() => RunAsync(async () =>
    {
        if (!await _confirmation.AskAsync(AppText.Get("granary.settings.mpv.preset.unloadTitle"), AppText.Get("granary.settings.mpv.preset.unloadBody"),
                AppText.Get("granary.settings.mpv.unload")))
        {
            return;
        }
        var result = await Task.Run(_presets.Unload);
        PresetMessage = result switch
        {
            MpvPresetUnload.Restored => AppText.Get("granary.settings.mpv.preset.unloaded"),
            MpvPresetUnload.Removed => AppText.Get("granary.settings.mpv.preset.unloadedRemoved"),
            MpvPresetUnload.NotCleanedUp => AppText.Get("granary.settings.mpv.preset.unloadedNotCleanedUp"),
            MpvPresetUnload.NothingToRestore => AppText.Get("granary.settings.mpv.preset.nothingToRestore"),
            _ => AppText.Get("granary.settings.mpv.preset.unloadFailed"),
        };
        if (result is MpvPresetUnload.Restored or MpvPresetUnload.Removed or MpvPresetUnload.NotCleanedUp)
        {
            _changes.Saved();
        }
        await ShowActiveAsync();
    });

    public Task DeletePresetAsync(MpvPresetRow preset) => RunAsync(async () =>
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (!await _confirmation.AskAsync(AppText.Get("granary.settings.mpv.preset.deleteTitle", ("name", preset.Name)),
                AppText.Get("granary.settings.mpv.preset.deleteBody"), AppText.Get("granary.settings.mpv.delete")))
        {
            return;
        }
        bool deleted = await Task.Run(() => _presets.Delete(preset.Name));
        PresetMessage = deleted
            ? AppText.Get("granary.settings.mpv.preset.deleted", ("name", preset.Name))
            : AppText.Get("granary.settings.mpv.preset.deleteFailed", ("name", preset.Name));
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
        ActivePresetText = active is null ? ""
            : active.IsChanged ? AppText.Get("granary.settings.mpv.preset.activeChanged", ("name", active.Name))
            : AppText.Get("granary.settings.mpv.preset.active", ("name", active.Name));
    }

    // An edit in Granary's editor can make the preset in use "changed". Active() logs a disk that fails and answers null.
    private void OnChanged(object? sender, EventArgs e)
    {
        ShowChanges();
        _ = ShowActiveAsync();
    }

    private void ShowChanges() => ChangeText = _changes.IsPending ? NextTitleText : "";
}

/// <summary>A preset in the list, with what its buttons are called for a screen reader.</summary>
public sealed record MpvPresetRow(string Name)
{
    public string LoadName => AppText.Get("granary.settings.mpv.preset.loadName", ("name", Name));

    public string DeleteName => AppText.Get("granary.settings.mpv.preset.deleteName", ("name", Name));
}
