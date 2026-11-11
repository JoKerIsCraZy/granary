using Granary.Core.Downloads;

namespace Granary.Tests.Fakes;

/// <summary>
/// Free space as the test scripts it: each question takes the next answer and the last one repeats (1 TB without any).
/// Null is a drive that is not there.
/// </summary>
internal sealed class FakeDiskSpace(params long?[] answers) : IDiskSpace
{
    private readonly Lock _gate = new();
    private readonly Queue<long?> _answers = new(answers.Length > 0 ? answers : [1L << 40]);
    private readonly List<string> _asked = [];

    /// <summary>The folders asked about so far, as a copy.</summary>
    public IReadOnlyList<string> Asked
    {
        get
        {
            lock (_gate)
            {
                return [.. _asked];
            }
        }
    }

    /// <summary>Answers <paramref name="free"/> from now on.</summary>
    public void Set(long? free)
    {
        lock (_gate)
        {
            _answers.Clear();
            _answers.Enqueue(free);
        }
    }

    public long? FreeBytes(string folder)
    {
        lock (_gate)
        {
            _asked.Add(folder);
            return _answers.Count > 1 ? _answers.Dequeue() : _answers.Peek();
        }
    }
}
