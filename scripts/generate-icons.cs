#:package SkiaSharp@3.119.4

// Regenerates TradeFoundry's private-app and tray icons from one definition.
//
//   dotnet run scripts/generate-icons.cs
//
// The mark is the public journal's favicon (a rising line on a rounded square, see
// PublicJournal/Contracts/PublicationAssets.cs) recolored with the private app's gold accent,
// so the public site (green) and the private app (gold) are easy to tell apart in tabs and the
// tray. The tray's "update available" variant adds a blue dot.

using SkiaSharp;

var root = FindRepositoryRoot();

const string Gold = "#d29922";     // --tf-gold, the private app's accent
const string Ink = "#0d1117";      // --tf-bg
const string Blue = "#58a6ff";     // --tf-blue

var outputs = new[]
{
    (Path: Path.Combine(root, "wwwroot", "favicon.svg"), Badge: false, Kind: "svg"),
    (Path: Path.Combine(root, "wwwroot", "favicon.ico"), Badge: false, Kind: "ico"),
    (Path: Path.Combine(root, "DesktopShell", "Assets", "TradeFoundry-update.ico"), Badge: true, Kind: "ico"),
};

foreach (var output in outputs)
{
    Directory.CreateDirectory(Path.GetDirectoryName(output.Path)!);
    if (output.Kind == "svg")
    {
        File.WriteAllText(output.Path, Svg(output.Badge));
    }
    else
    {
        File.WriteAllBytes(output.Path, Ico(output.Badge, [16, 20, 24, 32, 40, 48, 64, 256]));
    }

    Console.WriteLine($"wrote {Path.GetRelativePath(root, output.Path)}");
}

// Optional: preview PNGs for review, e.g. `dotnet run scripts/generate-icons.cs -- --preview out`.
if (args is ["--preview", var previewDirectory])
{
    Directory.CreateDirectory(previewDirectory);
    foreach (var badge in new[] { false, true })
    {
        foreach (var size in new[] { 16, 24, 32, 64, 256 })
        {
            File.WriteAllBytes(Path.Combine(previewDirectory, $"icon{(badge ? "-update" : "")}-{size}.png"), Png(badge, size));
        }
    }
}

static string Svg(bool badge) =>
    $"""
    <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><rect width="64" height="64" rx="14" fill="{Gold}"/><polyline points="12,44 25,31 35,38 52,18" fill="none" stroke="{Ink}" stroke-width="6" stroke-linecap="round" stroke-linejoin="round"/>{(badge ? $"""<circle cx="50" cy="14" r="14" fill="{Ink}"/><circle cx="50" cy="14" r="10.5" fill="{Blue}"/>""" : "")}</svg>

    """;

// Same geometry as the SVG, drawn natively at each size so small icons stay crisp.
static SKBitmap Render(bool badge, int size)
{
    var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul));
    using var canvas = new SKCanvas(bitmap);
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / 64f);

    using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColor.Parse(Gold) };
    canvas.DrawRoundRect(new SKRect(0, 0, 64, 64), 14, 14, fill);

    using var line = new SKPaint
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 6,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
        Color = SKColor.Parse(Ink)
    };
    using var path = new SKPath();
    path.MoveTo(12, 44);
    path.LineTo(25, 31);
    path.LineTo(35, 38);
    path.LineTo(52, 18);
    canvas.DrawPath(path, line);

    if (badge)
    {
        using var ring = new SKPaint { IsAntialias = true, Color = SKColor.Parse(Ink) };
        using var dot = new SKPaint { IsAntialias = true, Color = SKColor.Parse(Blue) };
        canvas.DrawCircle(50, 14, 14, ring);
        canvas.DrawCircle(50, 14, 10.5f, dot);
    }

    canvas.Flush();
    return bitmap;
}

static byte[] Png(bool badge, int size)
{
    using var bitmap = Render(badge, size);
    using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// ICO with 32-bit DIB frames for small sizes and a PNG frame at 256 px, which every consumer
// (Windows shell, the resource compiler, Inno Setup, Avalonia) accepts.
static byte[] Ico(bool badge, int[] sizes)
{
    var frames = sizes.Select(size => size >= 256 ? Png(badge, size) : Dib(badge, size)).ToArray();
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)sizes.Length);

    var offset = 6 + 16 * sizes.Length;
    for (var i = 0; i < sizes.Length; i++)
    {
        writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(frames[i].Length);
        writer.Write(offset);
        offset += frames[i].Length;
    }

    foreach (var frame in frames)
    {
        writer.Write(frame);
    }

    return stream.ToArray();
}

static byte[] Dib(bool badge, int size)
{
    using var bitmap = Render(badge, size);
    var maskStride = ((size + 31) / 32) * 4;
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);

    // BITMAPINFOHEADER; the height counts the XOR (color) and AND (mask) bitmaps together.
    writer.Write(40);
    writer.Write(size);
    writer.Write(size * 2);
    writer.Write((ushort)1);
    writer.Write((ushort)32);
    writer.Write(0);
    writer.Write(size * size * 4 + maskStride * size);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);

    for (var y = size - 1; y >= 0; y--)
    {
        for (var x = 0; x < size; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            writer.Write(pixel.Blue);
            writer.Write(pixel.Green);
            writer.Write(pixel.Red);
            writer.Write(pixel.Alpha);
        }
    }

    // All-zero AND mask: transparency comes from the alpha channel.
    writer.Write(new byte[maskStride * size]);
    return stream.ToArray();
}

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "TradeFoundry.csproj")))
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException("Run this from inside the TradeFoundry repository.");
}
