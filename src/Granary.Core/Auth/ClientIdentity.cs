using System.Reflection;
using System.Text.RegularExpressions;

namespace Granary.Core.Auth;

/// <summary>
/// How this installation identifies itself to servers (docs/api-contract.md, 6.4): sent as the
/// <c>X-Silo-Device-*</c> and <c>X-Silo-Client*</c> headers. Not secret; it authorizes nothing.
/// </summary>
public sealed partial record ClientIdentity(
    string DeviceId,
    string DeviceName,
    string DevicePlatform,
    string ClientName,
    string ClientVersion,
    string ClientChannel,
    string ClientFamily)
{
    public const string Platform = "windows";
    public const string Family = "desktop";

    /// <summary>
    /// Reads the installation's device id from <paramref name="deviceIdFile"/>, creating a random one on
    /// first use, and fills the rest from the machine and the entry assembly.
    /// </summary>
    public static ClientIdentity Load(string deviceIdFile, string clientName = "Granary")
    {
        string deviceId = ReadOrCreateDeviceId(deviceIdFile);
        var assembly = Assembly.GetEntryAssembly() ?? typeof(ClientIdentity).Assembly;
        string version = assembly.GetName().Version?.ToString(3) ?? "0.0.0";
#if DEBUG
        const string channel = "dev";
#else
        const string channel = "release";
#endif
        return new ClientIdentity(deviceId, Clamp(Environment.MachineName, 120), Platform, clientName, version, channel, Family);
    }

    public static string DefaultDeviceIdFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Granary", "device-id");

    private static string ReadOrCreateDeviceId(string path)
    {
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path).Trim();
            if (IsValidDeviceId(existing))
            {
                return existing;
            }
        }
        string created = Guid.NewGuid().ToString("D");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, created);
        return created;
    }

    // The server accepts up to 128 characters from [A-Za-z0-9._:-] (silo-server/internal/apiv2/device_header.go).
    internal static bool IsValidDeviceId(string value) => DeviceIdPattern().IsMatch(value);

    private static string Clamp(string value, int max) => value.Length <= max ? value : value[..max];

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,128}$")]
    private static partial Regex DeviceIdPattern();
}
