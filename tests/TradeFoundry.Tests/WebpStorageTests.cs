using ImageMagick;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public class WebpStorageTests
{
    [Theory]
    [InlineData(MagickFormat.Png, "image/png", true)]
    [InlineData(MagickFormat.Jpeg, "image/jpeg", false)]
    [InlineData(MagickFormat.WebP, "image/webp", true)]
    public async Task ConvertsUploadsUsingPersistedSettings(MagickFormat format, string mime, bool lossless)
    {
        var root = Path.Combine(Path.GetTempPath(), $"tf-webp-{Guid.NewGuid():N}");
        var journal = Guid.NewGuid();
        Directory.CreateDirectory(root);
        try
        {
            new JournalImageSettings { Lossless = lossless, Quality = 73, Method = 2 }.Save(root, journal);
            Assert.Equal(73, JournalImageSettings.Load(root, journal).Quality);
            Assert.Equal(lossless, JournalImageSettings.Load(root, journal).Lossless);
            Assert.True(JournalImageSettings.Load(root, Guid.NewGuid()).Lossless);
            using var original = new MagickImage(MagickColors.Red, 16, 12);
            using var input = new MemoryStream(original.ToByteArray(format));
            var store = new JournalImageFileStore(Path.Combine(root, "images"));
            var result = await store.SaveAsync(journal, input, mime, input.Length);
            Assert.EndsWith(".webp", result.StorageKey);
            Assert.Equal("image/webp", result.ContentType);
            var path = store.GetPath(journal, result.StorageKey)!;
            Assert.Equal(new FileInfo(path).Length, result.Length);
            using var decoded = new MagickImage(path);
            Assert.Equal(16u, decoded.Width);
            Assert.Equal(12u, decoded.Height);
            using var uploaded = new MagickImage(input.ToArray());
            if (lossless) Assert.Equal(0, uploaded.Compare(decoded, ErrorMetric.Absolute));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PreservesTransparencyAndCleansUpTruncatedImages()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tf-webp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var journal = Guid.NewGuid();
            var store = new JournalImageFileStore(Path.Combine(root, "images"));
            using var original = new MagickImage(MagickColors.Transparent, 2, 2);
            using var input = new MemoryStream(original.ToByteArray(MagickFormat.Png));
            var saved = await store.SaveAsync(journal, input, "image/png", input.Length);
            using var decoded = new MagickImage(store.GetPath(journal, saved.StorageKey)!);
            Assert.True(decoded.HasAlpha);
            Assert.Equal(0, original.Compare(decoded, ErrorMetric.Absolute));
            using var truncated = new MemoryStream(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(journal, truncated, "image/png", truncated.Length));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "images", journal.ToString("D"))));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RejectsInvalidSettings()
    {
        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            new JournalImageSettings { Quality = 101 }.Save(Path.GetTempPath(), Guid.NewGuid()));
        Assert.Throws<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            new JournalImageSettings { Method = -1 }.Save(Path.GetTempPath(), Guid.NewGuid()));
    }
}
