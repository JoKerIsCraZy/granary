using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Abstractions;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels.Settings;

/// <summary>
/// Settings → Playback → This PC → Edit: the viewer's own mpv.conf, edited inside Granary (Granary's own; Silo's clients
/// have no player configuration). It edits the real file, so profiles, includes and comments stay as written; the
/// check under the text is the player's own pre-scan, and it never stops a save.
/// </summary>
public sealed partial class MpvConfigEditorViewModel : ObservableObject
{
    /// <summary>The check when nothing in the text concerns Granary.</summary>
    public const string NothingToNote = "Nothing in it changes the options Granary keeps.";

    private const string NotTextMessage =
        "mpv.conf isn't UTF-8 text, so Granary doesn't edit it here, where saving could damage it. Use Open mpv.conf to change it in another editor.";

    private readonly MpvConfigFolder _folder;
    private readonly IMpvConfigCheck _check;
    private readonly MpvConfigChanges _changes;
    private readonly IConfirmation _confirmation;

    // The text as it is on disk (or the template), and the line break the file uses, which a save keeps.
    private string _saved = "";
    private bool _isNew;
    private string _lineBreak = "\r\n";

    public const string ChangedElsewhereMessage =
        "mpv.conf was changed outside Granary since you opened it, so this wasn't saved. Copy your edits, then close the editor and open it again.";

    public MpvConfigEditorViewModel(MpvConfigFolder folder, IMpvConfigCheck check, MpvConfigChanges changes, IConfirmation confirmation)
    {
        _folder = folder;
        _check = check;
        _changes = changes;
        _confirmation = confirmation;
        Notes = [NothingToNote];
    }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; } = true;

    /// <summary>The file could be read; a folder in its place, say, leaves nothing to edit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool CanEdit { get; private set; }

    /// <summary>A save is under way: the text is read-only, so nothing typed now is lost when the editor closes after it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool IsSaving { get; private set; }

    public bool CanSave => CanEdit && !IsSaving;

    /// <summary>The file's text as the viewer edits it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsavedChanges))]
    public partial string Text { get; set; } = "";

    /// <summary>The player's check of <see cref="Text"/>, in plain words, the most important first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Notes { get; private set; }

    /// <summary>Why the file could not be read or saved; empty otherwise.</summary>
    [ObservableProperty]
    public partial string Message { get; private set; } = "";

    /// <summary>The text differs from the file's, line breaks aside: a text box writes its own.</summary>
    public bool HasUnsavedChanges => CanEdit && Lines(Text) != Lines(_saved);

    public async Task LoadAsync()
    {
        IsLoading = true;
        // Off the UI thread: the disk can be slow.
        var content = await Task.Run(_folder.ReadConfig);
        if (content is null)
        {
            Message = _folder.ConfigFileIsFolder
                ? $"mpv.conf in {_folder.Directory} is a folder. Rename or remove it, then try again."
                : $"Couldn't read mpv.conf in {_folder.Directory}.";
        }
        else if (content.IsNotText)
        {
            Message = NotTextMessage;
        }
        else
        {
            _saved = content.Text;
            _isNew = content.IsNew;
            _lineBreak = content.Text.Contains("\r\n", StringComparison.Ordinal) || !content.Text.Contains('\n') ? "\r\n" : "\n";
            CanEdit = true;
            Text = content.Text;
        }
        IsLoading = false;
    }

    /// <summary>
    /// Writes the text to mpv.conf, with the file's own line breaks, and says the change is to come; true when the editor
    /// may close. A file that is there and unchanged is not written again.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        if (!CanEdit || IsSaving)
        {
            return false;
        }
        if (!_isNew && !HasUnsavedChanges)
        {
            return true;
        }
        IsSaving = true;
        try
        {
            string text = Text.ReplaceLineEndings(_lineBreak);
            // Open mpv.conf hands the file to another program, and the work done there must not go without a word.
            var now = await Task.Run(_folder.ReadConfig);
            if (ChangedElsewhere(now))
            {
                Message = ChangedElsewhereMessage;
                return false;
            }
            if (now is null || !await Task.Run(() => _folder.WriteConfig(text)))
            {
                // A write that had to wait stops when the file changes meanwhile, and that is what the viewer needs to hear.
                Message = ChangedElsewhere(await Task.Run(_folder.ReadConfig))
                    ? ChangedElsewhereMessage
                    : $"Couldn't save mpv.conf in {_folder.Directory}.";
                return false;
            }
            Message = "";
            _saved = text;
            _isNew = false;
            OnPropertyChanged(nameof(HasUnsavedChanges));
            _changes.Saved();
            return true;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool ChangedElsewhere(MpvConfigContent? now) => now is { IsNew: false } && (_isNew || now.IsNotText || now.Text != _saved);

    /// <summary>
    /// Whether the editor may close: nothing to lose, or the viewer chose to throw the edits away. Not while it saves, as the
    /// edits are on their way to the file by then and can't be thrown away any more.
    /// </summary>
    public Task<bool> ConfirmDiscardAsync() => IsSaving ? Task.FromResult(false) : HasUnsavedChanges
        ? _confirmation.AskAsync("Discard unsaved changes?", "Your edits to mpv.conf were never saved. Leaving now throws them away.", "Discard")
        : Task.FromResult(true);

    partial void OnTextChanged(string value)
    {
        var notes = _check.Notes(value);
        IReadOnlyList<string> next = notes.Count == 0 ? [NothingToNote] : notes;
        if (!next.SequenceEqual(Notes))
        {
            Notes = next;
        }
    }

    private static string Lines(string text) => text.ReplaceLineEndings("\n");
}
