using System.Globalization;
using System.Text.Json;
using Granary.Core.Api.Settings;

namespace Granary.Core.ViewModels.Settings;

/// <summary>An entry of a settings select: the value the setting stores and what the select shows.</summary>
public sealed record SettingChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Reading setting values as the settings pages need them, whatever JSON a server sent.</summary>
internal static class SettingValues
{
    public static string? Text(EffectiveSetting? setting) =>
        setting?.Value is { ValueKind: JsonValueKind.String } text && text.GetString() is { Length: > 0 } value ? value.Trim() : null;

    public static bool? Bool(EffectiveSetting? setting) => setting?.Value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    public static double? Number(EffectiveSetting? setting) =>
        setting?.Value is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out double value) && double.IsFinite(value) ? value : null;

    public static JsonElement Json(string value) => JsonSerializer.SerializeToElement(value, SettingsJsonContext.Default.String);

    public static JsonElement Json(bool value) => JsonSerializer.SerializeToElement(value, SettingsJsonContext.Default.Boolean);

    public static JsonElement Json(int value) => JsonSerializer.SerializeToElement(value, SettingsJsonContext.Default.Int32);

    public static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
