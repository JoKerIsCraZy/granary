namespace Granary.Core.Abstractions;

/// <summary>Windows' folder picker, for Settings → Downloads' Change.</summary>
public interface IFolderPicker
{
    /// <summary>The folder the viewer chose, as a full path; null when they cancelled.</summary>
    Task<string?> PickFolderAsync();
}
