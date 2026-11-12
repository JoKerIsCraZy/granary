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
