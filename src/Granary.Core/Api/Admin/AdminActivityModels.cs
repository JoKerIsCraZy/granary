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
