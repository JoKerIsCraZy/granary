using System.Net.Http.Headers;
using Granary.Core.Api.Catalog;

namespace Granary.Core.Api.Profiles;

/// <summary>
/// Household profiles (docs/api-contract.md, 6). The list and the PIN check run without profile headers; the household's
/// writes go out as its verified primary profile (<see cref="ManagingProfile"/>, 6.7).
/// </summary>
public sealed class ProfilesApi(SiloHttp http)
{
    private const string ProfilesPath = "api/v2/profiles";

    public Task<ProfileCollection> ListAsync(Uri server, CancellationToken cancellationToken = default) =>
        http.GetAsync(server, ProfilesPath, SiloAuth.Account, SiloJsonContext.Default.ProfileCollection, cancellationToken);

    /// <summary>
    /// Checks a PIN. A wrong PIN is <c>Valid = false</c>; five failures lock the profile for five minutes
    /// (429 with Retry-After). Each call counts, so it is never retried automatically.
    /// </summary>
    public Task<ProfileVerification> VerifyPinAsync(Uri server, string profileId, string pin, CancellationToken cancellationToken = default) =>
        http.PostAsync(server, $"{ProfilesPath}/{SiloHttp.Escape(profileId)}/verify-pin", SiloAuth.Account, new ProfilePinCheck(pin),
            SiloJsonContext.Default.ProfilePinCheck, SiloJsonContext.Default.ProfileVerification, cancellationToken);

    /// <summary>Creates a profile. A full household, or a name another profile has, answers 409 <c>conflict</c>.</summary>
    public Task<Profile> CreateAsync(Uri server, ProfileCreate profile, ManagingProfile manager, CancellationToken cancellationToken = default) =>
        http.SendAsManagerAsync(HttpMethod.Post, server, ProfilesPath, manager, profile, ProfilesJsonContext.Default.ProfileCreate,
            ProfilesJsonContext.Default.Profile, cancellationToken);

    /// <summary>Changes a profile; members left out stay. A name another profile has answers 409 <c>conflict</c>.</summary>
    public Task<Profile> UpdateAsync(Uri server, string profileId, ProfileUpdate profile, ManagingProfile manager,
        CancellationToken cancellationToken = default) =>
        http.SendAsManagerAsync(HttpMethod.Patch, server, PathOf(profileId), manager, profile, ProfilesJsonContext.Default.ProfileUpdate,
            ProfilesJsonContext.Default.Profile, cancellationToken);

    /// <summary>Deletes a profile; the primary one answers 409, one already gone 404.</summary>
    public Task DeleteAsync(Uri server, string profileId, ManagingProfile manager, CancellationToken cancellationToken = default) =>
        http.SendAsManagerAsync(HttpMethod.Delete, server, PathOf(profileId), manager, cancellationToken);

    /// <summary>
    /// Gives a profile a picture (<see cref="AvatarUpload"/>); the server makes its sizes. 503 when the server keeps no uploads.
    /// A picture the server would refuse is not sent, and like every household write it goes out only as
    /// <paramref name="manager"/>, while its account is signed in.
    /// </summary>
    /// <exception cref="AvatarRejectedException">The picture is too large or not a JPEG, PNG or WebP image; nothing was sent.</exception>
    public async Task<Profile> UploadAvatarAsync(Uri server, string profileId, ReadOnlyMemory<byte> image, string fileName,
        ManagingProfile manager, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(manager);
        string contentType = AvatarUpload.Check(image.Span);
        var part = new ReadOnlyMemoryContent(image);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var form = new MultipartFormDataContent { { part, "avatar", fileName } };
        return await http.SendFormAsManagerAsync(HttpMethod.Put, server, PathOf(profileId) + "/avatar", manager, form,
            ProfilesJsonContext.Default.Profile, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Takes a profile's uploaded picture away; a preset stays.</summary>
    public Task DeleteAvatarAsync(Uri server, string profileId, ManagingProfile manager, CancellationToken cancellationToken = default) =>
        http.SendAsManagerAsync(HttpMethod.Delete, server, PathOf(profileId) + "/avatar", manager, cancellationToken);

    /// <summary>
    /// The libraries a profile can be limited to: those <paramref name="manager"/> can see, the ones it hid itself included,
    /// as the web's editor lists them (<c>include_hidden</c>).
    /// </summary>
    public Task<LibraryCollection> ListLibrariesAsync(Uri server, ManagingProfile manager, CancellationToken cancellationToken = default) =>
        http.GetAsManagerAsync(server, "api/v2/user/libraries" + new QueryBuilder().Add("include_hidden", "true").ToString(), manager,
            SiloJsonContext.Default.LibraryCollection, cancellationToken);

    private static string PathOf(string profileId) => $"{ProfilesPath}/{SiloHttp.PathSegment(profileId, nameof(profileId))}";
}
