using System.IO;
using Microsoft.Extensions.Configuration;
using Utility.Server.Options;

namespace Utility.UI.Services;

/// <summary>
/// Read/write access to the effective configuration (appsettings + overrides).
/// Saving writes a user-scoped file rather than mutating the shipped appsettings.json.
/// </summary>
public interface ISettingsService
{
    string GetValue(string key, string fallback = "");

    bool GetBool(string key, bool fallback = false);


    void SetValue(string key, string value);

    /// <summary>Path the persisted overrides are written to.</summary>
    string UserSettingsPath { get; }
}

public sealed class SettingsService(IConfiguration configuration) : ISettingsService
{
    public string UserSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Utility",
        "appsettings.user.json");

    public string GetValue(string key, string fallback = "") => configuration[key] ?? fallback;

    public bool GetBool(string key, bool fallback = false) =>
        bool.TryParse(configuration[key], out var value) ? value : fallback;


    public void SetValue(string key, string value)
    {
        var path = UserSettingsPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Dictionary<string, object?> data = File.Exists(path)
            ? ReadJson(path)
            : new Dictionary<string, object?>();

        SetNested(data, key.Split('.'), value);

        var json = System.Text.Json.JsonSerializer.Serialize(data, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(path, json);
        configuration[key] = value;
    }

    private static Dictionary<string, object?> ReadJson(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
                   ?? new Dictionary<string, object?>();
        }
        catch
        {
            return new Dictionary<string, object?>();
        }
    }

    private static void SetNested(Dictionary<string, object?> root, string[] segments, string value)
    {
        var current = root;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var key = segments[i];

            if (!current.TryGetValue(key, out var node) ||
                node is not Dictionary<string, object?> child)
            {
                child = new Dictionary<string, object?>();
                current[key] = child;
            }

            current = child;
        }

        current[segments[^1]] = value;
    }
}
