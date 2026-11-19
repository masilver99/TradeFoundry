using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImageMagick;

namespace TradeFoundry.Services;

/// <summary>Content tuning matching libwebp's built-in presets (the same choices Paint.NET offers).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WebpPreset>))]
public enum WebpPreset
{
    Default,
    Picture,
    Photo,
    Drawing,
    Icon,
    Text
}

public sealed class JournalImageSettings
{
    public bool Lossless { get; set; }
    [Range(0, 100)] public int Quality { get; set; } = 85;
    [Range(0, 6)] public int Method { get; set; } = 4;
    [EnumDataType(typeof(WebpPreset))] public WebpPreset Preset { get; set; } = WebpPreset.Drawing;
    public bool SharpYuv { get; set; } = true;

    /// <summary>Applies the encoder settings to a Magick.NET image before writing WebP.</summary>
    public void ApplyTo(IMagickImage<byte> image)
    {
        image.Quality = (uint)Quality;
        Define(image, "lossless", Lossless ? "true" : "false");
        Define(image, "method", Method.ToString(CultureInfo.InvariantCulture));
        if (Lossless)
        {
            // Presets tune the lossy encoder; lossless only takes a coarse content hint.
            var hint = Preset switch
            {
                WebpPreset.Picture => "picture",
                WebpPreset.Photo => "photo",
                WebpPreset.Drawing or WebpPreset.Icon or WebpPreset.Text => "graph",
                _ => "default"
            };
            Define(image, "image-hint", hint);
            return;
        }

        // Values mirror libwebp's WebPConfigPreset (src/enc/config_enc.c).
        var (sns, filter, sharpness, segments, preprocessing) = Preset switch
        {
            WebpPreset.Picture => (80, 35, 4, 4, 0),
            WebpPreset.Photo => (80, 30, 3, 4, 2),
            WebpPreset.Drawing => (25, 10, 6, 4, 0),
            WebpPreset.Icon => (0, 0, 0, 4, 0),
            WebpPreset.Text => (0, 0, 0, 2, 0),
            _ => (50, 60, 0, 4, 0)
        };
        Define(image, "sns-strength", sns.ToString(CultureInfo.InvariantCulture));
        Define(image, "filter-strength", filter.ToString(CultureInfo.InvariantCulture));
        Define(image, "filter-sharpness", sharpness.ToString(CultureInfo.InvariantCulture));
        Define(image, "segments", segments.ToString(CultureInfo.InvariantCulture));
        Define(image, "preprocessing", preprocessing.ToString(CultureInfo.InvariantCulture));
        Define(image, "use-sharp-yuv", SharpYuv ? "true" : "false");
    }

    private static void Define(IMagickImage<byte> image, string name, string value) =>
        image.Settings.SetDefine(MagickFormat.WebP, name, value);

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
