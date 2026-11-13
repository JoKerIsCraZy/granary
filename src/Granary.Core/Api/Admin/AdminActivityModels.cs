namespace Granary.Core.Api.Admin;

/// <summary>A scheduled task of <c>GET /api/v2/admin/tasks</c>, as far as the activity indicator shows it.</summary>
public sealed record AdminTask
{
    /// <summary>The task's stable id, such as <c>refresh_metadata</c>.</summary>
    public string Key { get; init => field = value ?? ""; } = "";

    /// <summary>The server's English name for it.</summary>
    public string Name { get; init => field = value ?? ""; } = "";

    /// <summary>idle, running or cancelling, on the server that answered.</summary>
    public string State { get; init => field = value ?? ""; } = "";

    /// <summary>0 to 100; 0 while a running task does not say how far it is.</summary>
    public double Progress { get; init; }

    public bool IsRunning => State == "running";
}

public sealed record AdminTaskCollection
{
    public IReadOnlyList<AdminTask> Items { get; init => field = value ?? []; } = [];
}

/// <summary>
/// A library scan as the events stream's <c>scans</c> channel sends it: in the snapshot of the active ones and with every
/// <c>scan.*</c> event. There is no list of them to read.
/// </summary>
public sealed record AdminScanRun
{
    public string Id { get; init => field = value ?? ""; } = "";

    public long LibraryId { get; init; }

    /// <summary>library, subtree or file.</summary>
    public string Mode { get; init => field = value ?? ""; } = "";

    /// <summary>The folder or file of a scan that is not the whole library's.</summary>
    public string? Path { get; init; }

    /// <summary>accepted (queued), running, completed, failed or cancelled.</summary>
    public string Status { get; init => field = value ?? ""; } = "";

    public AdminScanResult? Result { get; init; }

    public bool IsRunning => Status == "running";

    /// <summary>Queued or running: what the indicator shows and counts.</summary>
    public bool IsActive => Status is "accepted" or "running";
}

/// <summary>How far a scan is, as far as the indicator says it.</summary>
public sealed record AdminScanResult
{
    /// <summary>The server's words for what the scan does now.</summary>
    public string? Message { get; init; }

    public long TotalFiles { get; init; }

    public long FilesProcessed { get; init; }
}

/// <summary><c>GET /api/v2/admin/downloads/preparations</c>: the queue that prepares offline downloads.</summary>
public sealed record AdminDownloadPreparationList
{
    /// <summary>How many jobs there are in each state, the ones past the read's limit too.</summary>
    public AdminDownloadPreparationCounts Counts { get; init => field = value ?? new(); } = new();

    /// <summary>Running jobs first, then those waiting to retry, the queue, the paused ones and the recent failures.</summary>
    public IReadOnlyList<AdminDownloadPreparation> Items { get; init => field = value ?? []; } = [];
}

public sealed record AdminDownloadPreparationCounts
{
    public int Running { get; init; }

    public int Queued { get; init; }

    public int Retrying { get; init; }

    public int Paused { get; init; }

    /// <summary>Jobs that failed in the last 24 hours.</summary>
    public int FailedRecent { get; init; }

    /// <summary>Jobs still to finish, as the web counts them: paused and failed ones wait on an administrator.</summary>
    public int Active => Running + Queued + Retrying;
}

/// <summary>One remux or transcode that turns a library file into an offline download, as far as the indicator shows it.</summary>
public sealed record AdminDownloadPreparation
{
    public string Id { get; init => field = value ?? ""; } = "";

    /// <summary>running, queued, retrying, paused or failed.</summary>
    public string State { get; init => field = value ?? ""; } = "";

    /// <summary>remux or transcode.</summary>
    public string Format { get; init => field = value ?? ""; } = "";

    public string MediaFileId { get; init => field = value ?? ""; } = "";

    public string MediaTitle { get; init => field = value ?? ""; } = "";

    public string? SeriesName { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public AdminDownloadPreparationOutput? Output { get; init; }

    /// <summary>Where the current or last attempt ran; null before a worker was chosen.</summary>
    public AdminDownloadPreparationWorker? Worker { get; init; }

    /// <summary>The latest reading of a running attempt; null until the first one.</summary>
    public AdminDownloadPreparationProgress? Progress { get; init; }

    public bool IsRunning => State == "running";
}

/// <param name="VideoCodec">The target video codec, or <c>copy</c>.</param>
/// <param name="Resolution">"1080p"; null when the output keeps the source's.</param>
public sealed record AdminDownloadPreparationOutput(string? VideoCodec, string? Resolution);

/// <param name="Kind">server (an API server) or node (a transcode node).</param>
/// <param name="NodeId">The transcode node's id when <paramref name="Kind"/> is node.</param>
/// <param name="Name">The node's name, or the API server's node id, when the attempt started.</param>
public sealed record AdminDownloadPreparationWorker(string? Kind, string? NodeId, string? Name);

/// <param name="EncodedSeconds">The output timeline written so far.</param>
/// <param name="DurationSeconds">The length progress is measured against; 0 when unknown.</param>
/// <param name="Speed">The encode rate as a multiple of realtime; 0 when not reported yet.</param>
public sealed record AdminDownloadPreparationProgress(double EncodedSeconds, double DurationSeconds, double Speed);

/// <summary>The payload of a <c>download_preparation.progress</c> event: a job's newest reading.</summary>
public sealed record AdminDownloadPreparationReading(string? Id, AdminDownloadPreparationProgress? Progress);
