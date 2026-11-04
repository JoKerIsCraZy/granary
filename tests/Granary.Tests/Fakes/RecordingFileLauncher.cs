using Granary.Core.Abstractions;

namespace Granary.Tests.Fakes;

/// <summary>An <see cref="IFileLauncher"/> that records what it was asked to open, and can fail.</summary>
internal sealed class RecordingFileLauncher : IFileLauncher
{
    public List<string> Opened { get; } = [];

    public bool Fails { get; set; }

    /// <summary>Fails only what it says yes to, e.g. files and not folders.</summary>
    public Func<string, bool>? FailWhen { get; set; }

    /// <summary>Holds an open of a file until set, as a slow editor start would.</summary>
    public ManualResetEventSlim? HoldFiles { get; set; }

    public bool OpenFile(string path) => Open("file " + path);

    public bool OpenFolder(string path) => Open("folder " + path);

    private bool Open(string what)
    {
        if (what.StartsWith("file ", StringComparison.Ordinal))
        {
            HoldFiles?.Wait(TimeSpan.FromSeconds(10));
        }
        if (Fails || FailWhen?.Invoke(what) == true)
        {
            return false;
        }
        Opened.Add(what);
        return true;
    }
}
