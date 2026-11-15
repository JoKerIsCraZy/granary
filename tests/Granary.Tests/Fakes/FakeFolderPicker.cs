using Granary.Core.Abstractions;

namespace Granary.Tests.Fakes;

/// <summary>A folder picker that answers <see cref="Picked"/> (null: the viewer cancelled) and counts how often it was shown.</summary>
internal sealed class FakeFolderPicker : IFolderPicker
{
    public string? Picked { get; set; }

    public int Shown { get; private set; }

    public Task<string?> PickFolderAsync()
    {
        Shown++;
        return Task.FromResult(Picked);
    }
}
