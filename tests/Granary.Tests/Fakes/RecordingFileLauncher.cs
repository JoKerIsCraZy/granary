using Granary.Core.Abstractions;

namespace Granary.Tests.Fakes;

/// <summary>An <see cref="IFileLauncher"/> that records what it was asked to open, and can fail.</summary>
internal sealed class RecordingFileLauncher : IFileLauncher
{
    public List<string> Opened { get; } = [];

    public bool Fails { get; set; }

    public bool OpenFile(string path) => Open("file " + path);

    public bool OpenFolder(string path) => Open("folder " + path);

    private bool Open(string what)
    {
        if (Fails)
        {
            return false;
        }
        Opened.Add(what);
        return true;
    }
}
