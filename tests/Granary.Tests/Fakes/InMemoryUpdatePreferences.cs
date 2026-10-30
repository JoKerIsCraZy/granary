using Granary.Core.Updates;

namespace Granary.Tests.Fakes;

/// <summary>A non-persistent <see cref="IUpdatePreferences"/> that keeps each value it was asked to save.</summary>
internal sealed class InMemoryUpdatePreferences(bool isAutomatic = true) : IUpdatePreferences
{
    public bool IsAutomatic { get; private set; } = isAutomatic;

    public List<bool> Saved { get; } = [];

    public Task SetAutomaticAsync(bool automatic)
    {
        IsAutomatic = automatic;
        Saved.Add(automatic);
        return Task.CompletedTask;
    }
}
