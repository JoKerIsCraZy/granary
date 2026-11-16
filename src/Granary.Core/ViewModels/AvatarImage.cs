namespace Granary.Core.ViewModels;

/// <summary>
/// A profile's picture as its avatar loads it: the address, and whether it is one of the preset drawings the server offers,
/// the only picture that may be drawn as SVG (<see cref="ProfileItemViewModel.AvatarIsServerPreset"/>). One value, so the two never
/// reach the view apart.
/// </summary>
/// <param name="Url">Absolute and presigned: load it without credentials.</param>
public sealed record AvatarImage(Uri Url, bool IsServerPreset);
