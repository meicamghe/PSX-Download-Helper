using System.Text.Json;
using System.Xml.Linq;
using System.Net;

namespace PSXDownloadHelper.Modern;

internal sealed record ImportedLegacySettings(string ListenAddress, int Port, string? Language, string? Theme, bool? UseCdn, bool? UseOffline, bool? UseLocalFiles);

internal static class LegacySettingsMigration
{
    private static readonly string[] SensitiveNames = ["Cookie", "cookie1", "cookie2", "ApPassword"];

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PSXDownloadHelper", "settings.json");

    public static ImportedLegacySettings Import(string xmlPath)
    {
        var document = XDocument.Load(xmlPath, LoadOptions.None);
        var values = document.Descendants()
            .Where(element => element.Name.LocalName == "setting")
            .Select(element => new
            {
                Name = (string?)element.Attribute("name"),
                Value = element.Elements().FirstOrDefault(e => e.Name.LocalName == "value")?.Value
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !SensitiveNames.Contains(item.Name!, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(item => item.Name!, item => item.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        string Get(string name, string fallback) => values.TryGetValue(name, out var value) ? value : fallback;
        bool? GetBool(string name) => bool.TryParse(Get(name, ""), out var value) ? value : null;

        var rawAddress = Get("Ip", "127.0.0.1").Trim();
        var address = IPAddress.TryParse(rawAddress, out var parsed) && (IPAddress.IsLoopback(parsed) || parsed.Equals(IPAddress.Any) || parsed.Equals(IPAddress.IPv6Any))
            ? parsed.ToString()
            : "127.0.0.1";

        if (!int.TryParse(Get("Port", "8080"), out var port) || port is < 1 or > 65535)
            port = 8080;

        var imported = new ImportedLegacySettings(
            address,
            port,
            Get("Language", "en"),
            Get("Theme", "#66C0B8"),
            GetBool("IsUseCdn"),
            GetBool("IsUseLixian"),
            GetBool("IsAutoFindFile"));

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var json = JsonSerializer.Serialize(imported, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
        return imported;
    }
}
