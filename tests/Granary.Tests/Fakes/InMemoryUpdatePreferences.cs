using Granary.Core.Updates;

namespace Granary.Tests.Fakes;

/// <summary>
/// A non-persistent <see cref="IUpdatePreferences"/> that keeps each value it was asked to save. With <see cref="Pending"/>
/// set, a save lasts until the test completes it, as a slow disk would.
/// </summary>
internal sealed class InMemoryUpdatePreferences(bool isAutomatic = true) : IUpdatePreferences
{
    public bool IsAutomatic { get; private set; } = isAutomatic;

    public List<bool> Saved { get; } = [];

    public TaskCompletionSource? Pending { get; set; }

    public Task SetAutomaticAsync(bool automatic)
    {
        IsAutomatic = automatic;
        Saved.Add(automatic);
        return Pending?.Task ?? Task.CompletedTask;
    }
}
