using Granary.Core.Downloads;

namespace Granary.Tests.Fakes;

/// <summary>Free space of <paramref name="free"/> bytes that notes whether each read ran on <paramref name="ui"/>'s thread.</summary>
internal sealed class ThreadNotingDiskSpace(SerialDispatcher ui, long free) : IDiskSpace
{
    private readonly Lock _gate = new();
    private readonly List<bool> _readOnUi = [];

    public IReadOnlyList<bool> ReadOnUi
    {
        get
        {
            lock (_gate)
            {
                return [.. _readOnUi];
            }
        }
    }

    public long? FreeBytes(string folder)
    {
        lock (_gate)
        {
            _readOnUi.Add(ui.HasThreadAccess);
        }
        return free;
    }
}
