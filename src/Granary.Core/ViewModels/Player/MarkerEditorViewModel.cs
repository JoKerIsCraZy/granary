using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Granary.Core.Api;
using Granary.Core.Api.Playback;
using Granary.Core.Playback;

namespace Granary.Core.ViewModels.Player;

/// <summary>Which end of a marked part a handle or a "Set" button moves.</summary>
public enum MarkerEdge
{
    Start,
    End,
}

/// <summary>
/// The player's marker editor, as Silo's web drives it (useMarkerEditor): a draft of one range per kind (intro, recap,
/// credits, preview) taken from the saved markers, changed by the seek bar's handles and the panel's buttons, and saved
/// with only the kinds the viewer touched, so a scanned marker is not rewritten as manual because another one was
/// edited.
/// </summary>
public sealed partial class MarkerEditorViewModel : ObservableObject
{
    /// <summary>The panel's order: about as they come in an episode.</summary>
    public static readonly string[] Kinds = [WatchMarkerKinds.Intro, WatchMarkerKinds.Recap, WatchMarkerKinds.Credits, WatchMarkerKinds.Preview];

    // A new part gets this long a span until its other end is set; a range never closes below the gap.
    private const double NewMarkerSpan = 60;
    private const double MinGap = 0.5;

    private readonly Func<IReadOnlyDictionary<string, WatchMarker?>, Task> _save;
    private readonly Dictionary<string, WatchMarker?> _original = [];
    private readonly Dictionary<string, WatchMarker?> _draft = [];
    private double _duration;

    public MarkerEditorViewModel(Func<IReadOnlyDictionary<string, WatchMarker?>, Task> save)
    {
        _save = save;
        Rows = [.. Kinds.Select(kind => new MarkerEditRow(kind, Label(kind), this))];
    }

    /// <summary>The panel's rows, one per kind.</summary>
    public IReadOnlyList<MarkerEditRow> Rows { get; }

    [ObservableProperty]
    public partial bool IsEditing { get; private set; }

    /// <summary>The kind whose row is open and whose handles the seek bar shows.</summary>
    [ObservableProperty]
    public partial string ActiveKind { get; private set; } = WatchMarkerKinds.Intro;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    public partial string Error { get; private set; } = "";

    /// <summary>A kind differs from what was saved: Save and Reset all have something to do.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsDirty { get; private set; }

    /// <summary>The draft as the seek bar shows it while editing: every kind that has a range.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PlaybackMarker> DraftMarkers { get; private set; } = [];

    /// <summary>Starts from <paramref name="markers"/>, the first of each kind, on the row of the first kind that has one.</summary>
    public void Begin(IReadOnlyList<PlaybackMarker> markers, double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(markers);
        _duration = durationSeconds;
        foreach (string kind in Kinds)
        {
            var first = markers.Where(m => m.Kind == kind).OrderBy(m => m.StartSeconds).FirstOrDefault();
            _original[kind] = _draft[kind] = first is null ? null : new WatchMarker(first.StartSeconds, first.EndSeconds);
        }
        ActiveKind = Kinds.FirstOrDefault(k => _draft[k] is not null) ?? WatchMarkerKinds.Intro;
        Error = "";
        IsEditing = true;
        Show();
    }

    /// <summary>Leaves the editor; the draft is dropped.</summary>
    [RelayCommand]
    public void Cancel()
    {
        IsEditing = false;
        Error = "";
    }

    public void SelectKind(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ActiveKind = kind;
        Show();
    }

    /// <summary>
    /// Moves <paramref name="edge"/> of <paramref name="kind"/> to <paramref name="seconds"/>: a kind without a range gets
    /// one a minute long on the other side; an end never passes the other one.
    /// </summary>
    public void SetEdge(string kind, MarkerEdge edge, double seconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        double upper = Math.Max(0, _duration > 0 ? _duration : seconds);
        double at = Math.Clamp(seconds, 0, upper);
        var existing = _draft.GetValueOrDefault(kind);
        WatchMarker next;
        if (existing is null)
        {
            double span = _duration > 0 ? Math.Min(NewMarkerSpan, _duration) : NewMarkerSpan;
            next = edge == MarkerEdge.Start ? new WatchMarker(at, Math.Min(upper, at + span)) : new WatchMarker(Math.Max(0, at - span), at);
        }
        else
        {
            next = edge == MarkerEdge.Start
                ? existing with { StartSeconds = Math.Min(at, existing.EndSeconds - MinGap) }
                : existing with { EndSeconds = Math.Max(at, existing.StartSeconds + MinGap) };
        }
        double start = Math.Clamp(next.StartSeconds, 0, upper);
        _draft[kind] = new WatchMarker(start, Math.Clamp(next.EndSeconds, start, upper));
        ActiveKind = kind;
        Error = "";
        Show();
    }

    /// <summary>Removes the kind's range: saved, the file has none until one is set again.</summary>
    public void Clear(string kind)
    {
        _draft[kind] = null;
        Error = "";
        Show();
    }

    /// <summary>Back to what the kind had when editing began.</summary>
    public void Reset(string kind)
    {
        _draft[kind] = _original.GetValueOrDefault(kind);
        Error = "";
        Show();
    }

    [RelayCommand]
    private void ResetAll()
    {
        foreach (string kind in Kinds)
        {
            _draft[kind] = _original.GetValueOrDefault(kind);
        }
        Error = "";
        Show();
    }

    public bool IsKindDirty(string kind) => _draft.GetValueOrDefault(kind) != _original.GetValueOrDefault(kind);

    private bool CanSave => IsDirty && !IsSaving;

    /// <summary>Saves the kinds that changed; the editor closes once the server took them.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var changes = Kinds.Where(IsKindDirty).ToDictionary(kind => kind, kind => _draft.GetValueOrDefault(kind));
        if (changes.Count == 0)
        {
            IsEditing = false;
            return;
        }
        IsSaving = true;
        Error = "";
        try
        {
            await _save(changes);
        }
        catch (Exception ex) when (ApiFailureText.IsRequestFailure(ex) || ex is InvalidOperationException)
        {
            Error = ex switch
            {
                SiloApiException { Problem.Detail: { Length: > 0 } detail } => detail,
                SiloApiException { Status: 403 } => "This account may not edit markers.",
                SiloApiException or InvalidOperationException => "Failed to save markers",
                _ => ApiFailureText.ForTransport(ex),
            };
            return;
        }
        finally
        {
            IsSaving = false;
        }
        foreach (string kind in Kinds)
        {
            _original[kind] = _draft.GetValueOrDefault(kind);
        }
        IsEditing = false;
        Show();
    }

    private void Show()
    {
        foreach (var row in Rows)
        {
            row.Show(_draft.GetValueOrDefault(row.Kind), row.Kind == ActiveKind, IsKindDirty(row.Kind));
        }
        IsDirty = Kinds.Any(IsKindDirty);
        DraftMarkers = [.. Kinds.Where(k => _draft.GetValueOrDefault(k) is { } r && r.EndSeconds > r.StartSeconds)
            .Select(k => new PlaybackMarker(k, _draft[k]!.StartSeconds, _draft[k]!.EndSeconds))
            .OrderBy(m => m.StartSeconds)];
    }

    /// <summary>"Credits / Outro": the credits marker is also the outro.</summary>
    public static string Label(string kind) => kind switch
    {
        WatchMarkerKinds.Intro => "Intro",
        WatchMarkerKinds.Recap => "Recap",
        WatchMarkerKinds.Credits => "Credits / Outro",
        _ => "Preview",
    };

    /// <summary>"3:07", "1:02:05", as the web's formatTime.</summary>
    public static string Time(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{time.Minutes}:{time.Seconds:00}");
    }
}

/// <summary>A row of the marker panel: the kind, its range, whether it is open, and what its buttons may do.</summary>
public sealed partial class MarkerEditRow(string kind, string label, MarkerEditorViewModel editor) : ObservableObject
{
    public string Kind { get; } = kind;

    public string Label { get; } = label;

    /// <summary>The kind's dot, as the seek bar tints it (sky, violet, amber, emerald 400).</summary>
    public string Color { get; } = kind switch
    {
        WatchMarkerKinds.Intro => "#38bdf8",
        WatchMarkerKinds.Recap => "#a78bfa",
        WatchMarkerKinds.Credits => "#fbbf24",
        _ => "#34d399",
    };

    public MarkerEditorViewModel Editor { get; } = editor;

    /// <summary>"0:03 – 0:12", or "Not set".</summary>
    [ObservableProperty]
    public partial string RangeText { get; private set; } = "Not set";

    [ObservableProperty]
    public partial bool HasRange { get; private set; }

    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    /// <summary>It differs from what was saved: Reset has something to do.</summary>
    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    /// <summary>"Intro, 0:03 – 0:12" for screen readers.</summary>
    public string AccessibleName => $"{Label}, {RangeText}";

    internal void Show(WatchMarker? range, bool isActive, bool isDirty)
    {
        RangeText = range is null ? "Not set" : $"{MarkerEditorViewModel.Time(range.StartSeconds)} – {MarkerEditorViewModel.Time(range.EndSeconds)}";
        HasRange = range is not null;
        IsActive = isActive;
        IsDirty = isDirty;
        OnPropertyChanged(nameof(AccessibleName));
    }
}
