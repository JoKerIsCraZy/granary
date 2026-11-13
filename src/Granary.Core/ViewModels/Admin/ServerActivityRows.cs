using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.Admin;

namespace Granary.Core.ViewModels.Admin;

/// <summary>The rows of the activity panel.</summary>
internal static class ServerActivityRows
{
    /// <summary>
    /// Makes <paramref name="rows"/> the rows of <paramref name="items"/>, in their order. A row whose item is still there
    /// stays the same object and is only updated, so a row the keyboard is on keeps its focus while its numbers change.
    /// </summary>
    public static void Sync<TItem, TRow>(ObservableCollection<TRow> rows, IReadOnlyList<TItem> items, Func<TItem, string> keyOf,
        Func<TRow, string> rowKeyOf, Func<TItem, TRow> create, Action<TRow, TItem> update)
    {
        var wanted = items.Select(keyOf).ToHashSet(StringComparer.Ordinal);
        for (int index = rows.Count - 1; index >= 0; index--)
        {
            if (!wanted.Contains(rowKeyOf(rows[index])))
            {
                rows.RemoveAt(index);
            }
        }
        for (int index = 0; index < items.Count; index++)
        {
            string key = keyOf(items[index]);
            int at = index;
            while (at < rows.Count && rowKeyOf(rows[at]) != key)
            {
                at++;
            }
            if (at == rows.Count)
            {
                rows.Insert(index, create(items[index]));
            }
            else if (at != index)
            {
                rows.Move(at, index);
            }
            update(rows[index], items[index]);
        }
        // Items that share a key leave rows behind the last item.
        while (rows.Count > items.Count)
        {
            rows.RemoveAt(rows.Count - 1);
        }
    }
}

/// <summary>The streams of one play method: "2 Direct Play".</summary>
public sealed partial class ActivityStreamRow(string method) : ObservableObject
{
    /// <summary>One of <see cref="AdminPlayMethod"/>'s methods; the view colours the row's dot by it.</summary>
    public string Method { get; } = method;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    public partial int Count { get; set; }

    public string Text => $"{Count} {AdminPlayMethod.Label(Method)}";
}

/// <summary>A running task: its name, and "42%" over a bar once it says how far it is, "Running" until then.</summary>
public sealed partial class ActivityTaskRow(string key) : ObservableObject
{
    public string Key { get; } = key;

    /// <summary>The server's English name for the task; its key without one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadText))]
    public partial string Name { get; private set; } = key;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress), nameof(IsWorking), nameof(ProgressValue), nameof(StatusText), nameof(ReadText))]
    public partial double Progress { get; private set; }

    /// <summary>The task says how far it is: a number and a bar, as the web's <c>progress &gt; 0</c>.</summary>
    public bool HasProgress => Progress > 0;

    /// <summary>It runs without saying how far: a spinner and "Running".</summary>
    public bool IsWorking => !HasProgress;

    /// <summary>The bar's fill, 0 to 100.</summary>
    public double ProgressValue => ServerActivityText.ClampPercent(Progress);

    public string StatusText => HasProgress ? ServerActivityText.TaskProgress(Progress) : "Running";

    /// <summary>The row as one text for a screen reader.</summary>
    public string ReadText => $"{Name}, {StatusText}";

    internal void Show(AdminTask task)
    {
        Name = task.Name.Length > 0 ? task.Name : task.Key;
        Progress = task.Progress;
    }
}

/// <summary>
/// A library scan, queued or running: the library, "Scanning…" or "Queued", what it covers ("Subtree · /media/tv/Dark")
/// and, once it says so, how far it is.
/// </summary>
public sealed partial class ActivityScanRow(string id) : ObservableObject
{
    public string Id { get; } = id;

    /// <summary>The library's name, or "Library #3" while it is not known.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadText))]
    public partial string LibraryName { get; private set; } = "";

    /// <summary>The scan runs: a spinner and "Scanning…". Otherwise it waits its turn: "Queued".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQueued), nameof(StatusText), nameof(ReadText))]
    public partial bool IsRunning { get; private set; }

    public bool IsQueued => !IsRunning;

    public string StatusText => IsRunning ? "Scanning…" : "Queued";

    /// <summary>"Full library", or "Subtree · /media/tv/Dark".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadText))]
    public partial string DetailText { get; private set; } = "";

    /// <summary>"Matching files · 1,200 / 4,800 (25%)"; empty while the scan says nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgressText), nameof(ReadText))]
    public partial string ProgressText { get; private set; } = "";

    public bool HasProgressText => ProgressText.Length > 0;

    /// <summary>The row as one text for a screen reader.</summary>
    public string ReadText => string.Join(", ", new[] { LibraryName, StatusText, DetailText, ProgressText }.Where(part => part.Length > 0));

    internal void Show(AdminScanRun scan, FrozenDictionary<string, string>? libraryNames)
    {
        string library = scan.LibraryId.ToString(CultureInfo.InvariantCulture);
        LibraryName = libraryNames is not null && libraryNames.TryGetValue(library, out string? name) && name.Length > 0 ? name : $"Library #{library}";
        IsRunning = scan.IsRunning;
        string mode = ServerActivityText.ScanMode(scan.Mode);
        DetailText = string.IsNullOrEmpty(scan.Path) ? mode : $"{mode} · {scan.Path}";
        ProgressText = ServerActivityText.ScanProgress(scan.Result);
    }
}

/// <summary>
/// A download being prepared: its title, "42%" over a bar once its progress is known (a spinner with "Encoding" or
/// "Remuxing" until then), and "Transcode 1080p H.264 · gpu-1 · about 12 min left".
/// </summary>
public sealed partial class ActivityPreparationRow(string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressName), nameof(ReadText))]
    public partial string Title { get; private set; } = "";

    /// <summary>The job's progress is known: a number and a bar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    public partial bool HasProgress { get; private set; }

    /// <summary>It runs without a known progress: a spinner and <see cref="StatusText"/>.</summary>
    public bool IsWorking => !HasProgress;

    /// <summary>The bar's fill, 0 to 100.</summary>
    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    /// <summary>"42%" (rounded down, as the web), or "Encoding" / "Remuxing".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadText))]
    public partial string StatusText { get; private set; } = "";

    /// <summary>"Transcode 1080p H.264 · gpu-1 · about 12 min left".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadText))]
    public partial string DetailText { get; private set; } = "";

    /// <summary>The bar's name for a screen reader, as the web's: "Dark S01E03 progress".</summary>
    public string ProgressName => $"{Title} progress";

    /// <summary>The row as one text for a screen reader.</summary>
    public string ReadText => string.Join(", ", new[] { Title, StatusText, DetailText }.Where(part => part.Length > 0));

    internal void Show(AdminDownloadPreparation job)
    {
        Title = ServerActivityText.PreparationTitle(job);
        double? percent = ServerActivityText.PreparationPercent(job.Progress);
        HasProgress = percent is not null;
        ProgressValue = percent ?? 0;
        StatusText = percent is { } known ? string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(known):0}%")
            : job.Format == "remux" ? "Remuxing"
            : "Encoding";
        DetailText = string.Join(" · ", new[]
        {
            ServerActivityText.PreparationKind(job),
            ServerActivityText.PreparationWorker(job.Worker),
            ServerActivityText.PreparationRemaining(job.Progress),
        }.Where(part => part.Length > 0));
    }
}
