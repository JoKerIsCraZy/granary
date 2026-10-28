using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Granary.Core.Api.WatchParty;

/// <summary>
/// An identifier the server writes as a string over HTTP and as a number on the room socket (<c>user_id</c>,
/// <c>selected_file_id</c>): read either way as its text, written back as a string.
/// </summary>
public sealed class FlexibleIdConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number when reader.TryGetInt64(out long id) => id.ToString(CultureInfo.InvariantCulture),
        JsonTokenType.Null => null,
        _ => throw new JsonException("An identifier is a string or a whole number."),
    };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
