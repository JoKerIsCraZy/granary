namespace Granary.Core.ViewModels;

/// <summary>
/// Whether a profile's avatar shows its picture or its letter, as Silo's apps keep it: the letter until the picture opened,
/// and again when it fails or there is none. The server signs a picture's address anew every quarter of an hour
/// (docs/api-contract.md, 7.10), so a picture already shown stays while the new address loads, and only the newest load
/// counts. The view loads what <see cref="Show"/> asks for and reports back with its number.
/// </summary>
public sealed class AvatarPicture
{
    private Uri? _url;
    private int _load;

    /// <summary>The picture shows; otherwise the letter does.</summary>
    public bool ShowsPicture { get; private set; }

    /// <summary>
    /// The avatar is now this picture, or none. Returns the number of the load to start, or null when there is nothing to
    /// load: no picture, which shows the letter at once, or the one already shown or loading.
    /// </summary>
    public int? Show(Uri? url)
    {
        if (url == _url)
        {
            return null;
        }
        _url = url;
        _load++;
        if (url is null)
        {
            ShowsPicture = false;
            return null;
        }
        return _load;
    }

    /// <summary>Load <paramref name="load"/> opened its picture. True when it is the newest, and the picture shows.</summary>
    public bool Opened(int load)
    {
        if (load != _load)
        {
            return false;
        }
        ShowsPicture = true;
        return true;
    }

    /// <summary>
    /// Load <paramref name="load"/> could not get or decode its picture. True when it is the newest, and the letter shows; its
    /// address is tried again when it comes again.
    /// </summary>
    public bool Failed(int load)
    {
        if (load != _load)
        {
            return false;
        }
        ShowsPicture = false;
        _url = null;
        return true;
    }
}
