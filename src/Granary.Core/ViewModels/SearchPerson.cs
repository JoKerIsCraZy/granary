using Granary.Core.Api.Catalog;
using Granary.Core.Api.Images;

namespace Granary.Core.ViewModels;

/// <summary>A person found by Search: a round photo card with initials while there is no photo.</summary>
/// <param name="PhotoUrl">Absolute and presigned: load it without credentials.</param>
public sealed record SearchPerson(string Id, string Name, Uri? PhotoUrl)
{
    public string Initials => ProfileItemViewModel.InitialsOf(Name);

    public static SearchPerson From(Person person, Uri serverBase)
    {
        ArgumentNullException.ThrowIfNull(person);
        return new(person.Id, person.Name, ArtworkUrl.Resolve(serverBase, person.PhotoUrl));
    }

    // A record would print the photo URL, and with it the signature.
    public override string ToString() => Name;
}
