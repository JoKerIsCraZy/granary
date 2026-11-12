using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Granary.Core.Api.Admin;

namespace Granary.Core.ViewModels.Admin;

/// <summary>The numbers of the activity indicator, as the web writes them (<c>formatBadgeCount</c>).</summary>
public static class ServerActivityText
{
    private const int MaxCount = 99;

    /// <summary>"3", "99+" past 99; a count that is only a lower bound always ends in "+".</summary>
    public static string Count(int count, bool isLowerBound = false) =>
        count > MaxCount ? $"{MaxCount}+" : count.ToString(CultureInfo.InvariantCulture) + (isLowerBound ? "+" : "");

    /// <summary>
    /// A task's progress, 0 to 100, as the web's <c>formatTaskProgress</c>: "42%", "12.5%", and never "100.0%" for a task
    /// that reports just under 100 while it still runs.
    /// </summary>
    public static string TaskProgress(double progress)
    {
        double clamped = ClampPercent(progress);
        if (clamped == Math.Floor(clamped))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{clamped:0}%");
        }
        double shown = Math.Round(clamped, 1, MidpointRounding.AwayFromZero) >= 100 ? 99.9 : clamped;
        return string.Create(CultureInfo.InvariantCulture, $"{shown:0.0}%");
    }

    /// <summary>0 to 100; what is no number counts as 0.</summary>
    public static double ClampPercent(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;

    /// <summary>"3 more scans queued" for the scans the panel does not list, "At least 475 more scans queued" when their number is a lower bound.</summary>
    public static string MoreScans(int count, bool isLowerBound)
    {
        string more = string.Create(CultureInfo.InvariantCulture, $"{count:N0} more {(count == 1 ? "scan" : "scans")} queued");
        return isLowerBound ? "At least " + more : more;
    }

    /// <summary>What a scan covers: "Full library", "Subtree" or "Single file"; a mode the web has no words for as the server names it.</summary>
    public static string ScanMode(string mode) => mode switch
    {
        "library" => "Full library",
        "subtree" => "Subtree",
        "file" => "Single file",
        _ => mode,
    };

    /// <summary>
    /// How far a scan is, as the web's <c>formatScanProgress</c>: "Matching files · 1,200 / 4,800 (25%)" once it counts
    /// files ("Processing files" when it does not say what it does), else what it says, else nothing.
    /// </summary>
    public static string ScanProgress(AdminScanResult? result)
    {
        if (result is null)
        {
            return "";
        }
        if (result.TotalFiles != 0 && result.FilesProcessed != 0)
        {
            double percent = Math.Clamp(Math.Round((double)result.FilesProcessed / result.TotalFiles * 100, MidpointRounding.AwayFromZero), 0, 100);
            string message = string.IsNullOrEmpty(result.Message) ? "Processing files" : result.Message;
            return string.Create(CultureInfo.InvariantCulture, $"{message} · {result.FilesProcessed:N0} / {result.TotalFiles:N0} ({percent:0}%)");
        }
        return result.Message ?? "";
    }
}

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
