using ImageMagick;

namespace TradeFoundry.Services;

public sealed record StoredJournalImage(string StorageKey, string ContentType, long Length);

public sealed class JournalImageFileStore
{
    public const long MaxImageLength = 10 * 1024 * 1024;

    private readonly string _root;

    public JournalImageFileStore(string root) => _root = Path.GetFullPath(root);

    public async Task<StoredJournalImage> SaveAsync(
        Guid journalId,
        Stream content,
        string contentType,
        long declaredLength,
        string subject = "Images",
        CancellationToken cancellationToken = default)
    {
        if (declaredLength <= 0 || declaredLength > MaxImageLength)
            throw new InvalidOperationException($"{subject} must be between 1 byte and 10 MB.");

        var imageNoun = subject.EndsWith('s') ? subject[..^1].ToLowerInvariant() : subject.ToLowerInvariant();
        var (normalizedType, extension) = contentType.Trim().ToLowerInvariant() switch
        {
            "image/png" => ("image/png", ".png"),
            "image/jpeg" or "image/jpg" => ("image/jpeg", ".jpg"),
            "image/webp" => ("image/webp", ".webp"),
            _ => throw new InvalidOperationException($"Only PNG, JPEG, and WebP {subject.ToLowerInvariant()} are supported.")
        };

        var directory = Path.Combine(_root, journalId.ToString("D"));
        Directory.CreateDirectory(directory);
        var storageKey = $"{Guid.NewGuid():N}{extension}";
        var path = Path.Combine(directory, storageKey);
        try
        {
            long actualLength;
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                actualLength = await CopyBoundedAsync(content, output, subject, cancellationToken);
            }

            if (actualLength <= 0)
                throw new InvalidOperationException($"{subject} must be between 1 byte and 10 MB.");

            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (!await HasImageSignatureAsync(input, normalizedType, cancellationToken))
                    throw new InvalidOperationException($"The {imageNoun} content does not match its image type.");
            }

            var settings = JournalImageSettings.Load(Path.GetDirectoryName(_root)!, journalId);
            var webpKey = $"{Guid.NewGuid():N}.webp";
            var webpPath = Path.Combine(directory, webpKey);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var format = normalizedType switch { "image/png" => MagickFormat.Png, "image/jpeg" => MagickFormat.Jpeg, _ => MagickFormat.WebP };
                using var info = new MagickImage();
                info.Ping(path, new MagickReadSettings { Format = format });
                if (info.Width > 16383 || info.Height > 16383 || (long)info.Width * info.Height > 40_000_000)
                    throw new InvalidOperationException("Images must contain at most 40 million pixels and measure at most 16,383 pixels per side.");
                using var frames = new MagickImageCollection();
                frames.Read(path, new MagickReadSettings { Format = format, FrameCount = 2 });
                if (frames.Count != 1)
                    throw new InvalidOperationException("Animated images are not supported.");
                var image = frames[0];
                image.Quality = (uint)settings.Quality;
                image.Settings.SetDefine(MagickFormat.WebP, "lossless", settings.Lossless ? "true" : "false");
                image.Settings.SetDefine(MagickFormat.WebP, "method", settings.Method.ToString(System.Globalization.CultureInfo.InvariantCulture));
                image.Write(webpPath, MagickFormat.WebP);
                cancellationToken.ThrowIfCancellationRequested();
                var length = new FileInfo(webpPath).Length;
                File.Delete(path);
                return new StoredJournalImage(webpKey, "image/webp", length);
            }
            catch (MagickException ex)
            {
                File.Delete(webpPath);
                throw new InvalidOperationException("The image could not be decoded. Upload a valid PNG, JPEG, or WebP image.", ex);
            }
            catch
            {
                File.Delete(webpPath);
                throw;
            }
        }
        catch
        {
            Delete(journalId, storageKey);
            throw;
        }
    }

    public string? GetPath(Guid journalId, string storageKey)
    {
        var directory = Path.GetFullPath(Path.Combine(_root, journalId.ToString("D"))) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(directory, storageKey));
        return path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
    }

    public void Delete(Guid journalId, string storageKey)
    {
        var path = GetPath(journalId, storageKey);
        if (path is not null) File.Delete(path);
    }

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, string subject, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return total;
            total += read;
            if (total > MaxImageLength) throw new InvalidOperationException($"{subject} must be between 1 byte and 10 MB.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task<bool> HasImageSignatureAsync(Stream stream, string contentType, CancellationToken cancellationToken)
    {
        var header = new byte[12];
        var read = 0;
        while (read < header.Length)
        {
            var count = await stream.ReadAsync(header.AsMemory(read, header.Length - read), cancellationToken);
            if (count == 0) break;
            read += count;
        }

        return contentType switch
        {
            "image/png" => read >= 8 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/jpeg" => read >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff,
            "image/webp" => read >= 12 && header[..4].SequenceEqual("RIFF"u8.ToArray()) && header[8..12].SequenceEqual("WEBP"u8.ToArray()),
            _ => false
        };
    }
}
