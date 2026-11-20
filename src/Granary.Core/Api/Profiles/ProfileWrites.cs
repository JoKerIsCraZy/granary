using System.Text.Json;
using System.Text.Json.Serialization;

namespace Granary.Core.Api.Profiles;

/// <summary>
/// <c>POST /api/v2/profiles</c>, as the web's <c>buildProfileRequestFromDraft</c> sends it: a member with nothing to set is
/// left out, as the contract takes no null here. The PIN is a secret and never shows in <see cref="ToString"/>.
/// </summary>
public sealed record ProfileCreate(string Name)
{
    /// <summary>A preset, as <c>preset:dicebear:identicon:cosmic-otter</c>.</summary>
    public string? Avatar { get; init; }

    public string? Pin { get; init; }

    public bool IsChild { get; init; }

    public string? MaxContentRating { get; init; }

    public int? MaxAdvisoryAge { get; init; }

    public bool? RequireAdvisoryAge { get; init; }

    /// <summary>Only <c>false</c> is sent; left out, the profile may request.</summary>
    public bool? RequestsAllowed { get; init; }

    public string? MaxPlaybackQuality { get; init; }

    public bool LibraryRestrictionsEnabled { get; init; }

    /// <summary>Library ids as strings; empty without a restriction.</summary>
    public IReadOnlyList<string> AllowedLibraryIds { get; init; } = [];

    public override string ToString() => $"ProfileCreate {{ Pin = {(Pin is null ? "none" : "<redacted>")} }}";
}

/// <summary>
/// A member of <see cref="ProfileUpdate"/> that can be left as it is, cleared (sent as null) or set: the contract's PATCH
/// tells the three apart, and Silo's own serializer cannot (it leaves nulls out).
/// </summary>
public readonly record struct Change<T>
{
    private Change(bool isSent, T? value)
    {
        IsSent = isSent;
        Value = value;
    }

    /// <summary>Sent at all; the default leaves the member out, so the server keeps what it has.</summary>
    public bool IsSent { get; }

    /// <summary>What is sent; null clears.</summary>
    public T? Value { get; }

    public static Change<T> Keep => default;

    public static Change<T> Clear => new(true, default);

    public static Change<T> To(T value) => new(true, value);
}

/// <summary>
/// <c>PATCH /api/v2/profiles/{id}</c>, as the web's <c>buildProfileUpdateFromDraft</c>: every member the editor owns is sent,
/// a cleared one as null; the PIN only when it changes, and the avatar not while an uploaded picture stays or goes through
/// its own call. The advisory and requests members go only to a server that reports them (<see cref="ProfileCollection"/>).
/// </summary>
[JsonConverter(typeof(ProfileUpdateConverter))]
public sealed record ProfileUpdate(string Name)
{
    public Change<string> Avatar { get; init; }

    /// <summary>A new PIN, or <see cref="Change{T}.Clear"/> to remove it. A secret.</summary>
    public Change<string> Pin { get; init; }

    public bool IsChild { get; init; }

    /// <summary>Null clears the ceiling.</summary>
    public string? MaxContentRating { get; init; }

    public Change<int> MaxAdvisoryAge { get; init; }

    public bool? RequireAdvisoryAge { get; init; }

    public bool? RequestsAllowed { get; init; }

    /// <summary>Null clears the ceiling.</summary>
    public string? MaxPlaybackQuality { get; init; }

    public bool LibraryRestrictionsEnabled { get; init; }

    public IReadOnlyList<string> AllowedLibraryIds { get; init; } = [];

    public override string ToString() => $"ProfileUpdate {{ Pin = {(Pin.IsSent ? "<redacted>" : "kept")} }}";
}

/// <summary>Writes <see cref="ProfileUpdate"/> member by member, nulls included. Granary never reads one.</summary>
internal sealed class ProfileUpdateConverter : JsonConverter<ProfileUpdate>
{
    public override ProfileUpdate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("A profile update is only written.");

    public override void Write(Utf8JsonWriter writer, ProfileUpdate value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        WriteChange(writer, "avatar", value.Avatar, (w, v) => w.WriteStringValue(v));
        WriteChange(writer, "pin", value.Pin, (w, v) => w.WriteStringValue(v));
        writer.WriteBoolean("is_child", value.IsChild);
        WriteNullable(writer, "max_content_rating", value.MaxContentRating);
        if (value.MaxAdvisoryAge.IsSent)
        {
            if (value.MaxAdvisoryAge.Value is { } age && age > 0)
            {
                writer.WriteNumber("max_advisory_age", age);
            }
            else
            {
                writer.WriteNull("max_advisory_age");
            }
        }
        if (value.RequireAdvisoryAge is { } require)
        {
            writer.WriteBoolean("require_advisory_age", require);
        }
        if (value.RequestsAllowed is { } requests)
        {
            writer.WriteBoolean("requests_allowed", requests);
        }
        WriteNullable(writer, "max_playback_quality", value.MaxPlaybackQuality);
        writer.WriteBoolean("library_restrictions_enabled", value.LibraryRestrictionsEnabled);
        writer.WriteStartArray("allowed_library_ids");
        foreach (string id in value.AllowedLibraryIds)
        {
            writer.WriteStringValue(id);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteChange(Utf8JsonWriter writer, string name, Change<string> change, Action<Utf8JsonWriter, string> write)
    {
        if (!change.IsSent)
        {
            return;
        }
        writer.WritePropertyName(name);
        if (change.Value is { } value)
        {
            write(writer, value);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}

/// <summary>The wire format of <see cref="SiloJsonContext"/> for the household's writes (<see cref="ProfilesApi"/>).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(ProfileCreate))]
[JsonSerializable(typeof(ProfileUpdate))]
[JsonSerializable(typeof(Profile))]
internal sealed partial class ProfilesJsonContext : JsonSerializerContext;
