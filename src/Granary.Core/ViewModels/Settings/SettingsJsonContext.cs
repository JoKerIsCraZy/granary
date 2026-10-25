using System.Text.Json.Serialization;

namespace Granary.Core.ViewModels.Settings;

/// <summary>Plain setting values written by the settings pages.</summary>
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
