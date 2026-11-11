using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace TradeFoundry.Services;

public sealed class JournalImageSettings
{
    public bool Lossless { get; set; } = true;
    [Range(0, 100)] public int Quality { get; set; } = 85;
    [Range(0, 6)] public int Method { get; set; } = 4;

    private static string SettingsPath(string root, Guid journalId) =>
        Path.Combine(root, "image-settings", $"{journalId:D}.json");

    public static JournalImageSettings Load(string root, Guid journalId)
    {
        var path = SettingsPath(root, journalId);
        var settings = File.Exists(path)
            ? JsonSerializer.Deserialize<JournalImageSettings>(File.ReadAllText(path)) ?? new()
            : new JournalImageSettings();
        Validator.ValidateObject(settings, new ValidationContext(settings), true);
        return settings;
    }

    public void Save(string root, Guid journalId)
    {
        Validator.ValidateObject(this, new ValidationContext(this), true);
        var path = SettingsPath(root, journalId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
