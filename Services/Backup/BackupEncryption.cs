using System.Security.Cryptography;
using System.Text;
using Age;
using Age.Recipients;

namespace TradeFoundry.Services.Backup;

/// <summary>
/// Encrypts whole backup archives in the standard age format with a passphrase (scrypt), so a backup
/// can also be opened without TradeFoundry: <c>age -d backup.tfbackup &gt; backup.zip</c>.
/// </summary>
public static class BackupEncryption
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("age-encryption.org/v1");

    public const int MinimumPassphraseLength = 12;

    /// <summary>True when the file starts with the age header.</summary>
    public static bool IsEncrypted(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[Magic.Length];
        return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length && buffer.AsSpan().SequenceEqual(Magic);
    }

    /// <summary>Encrypts <paramref name="source"/> into a new file at <paramref name="target"/> and flushes it to disk.</summary>
    public static void Encrypt(string source, string target, string passphrase, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        var total = Math.Max(1, input.Length);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024);
        using (var encrypted = AgeEncrypt.EncryptReader(new ProgressReadStream(input, read => progress?.Report((double)read / total), cancellationToken), new ScryptRecipient(passphrase)))
        {
            encrypted.CopyTo(output, 1024 * 1024);
        }

        output.Flush(flushToDisk: true);
    }

    /// <summary>Decrypts the whole file and checks that it is exactly the plaintext with SHA-256 <paramref name="expectedSha256"/>.</summary>
    public static void VerifyDecryptsTo(string path, string passphrase, string expectedSha256, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        var total = Math.Max(1, input.Length);
        using var counting = new ProgressReadStream(input, read => progress?.Report((double)read / total), cancellationToken);
        using var decrypted = Open(() => AgeEncrypt.DecryptReader(counting, new ScryptRecipient(passphrase)));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = decrypted.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
        if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The encrypted backup does not decrypt to the archive that was written.");
    }

    /// <summary>
    /// Opens an encrypted archive as a seekable plaintext stream, decrypting chunks as they are read, so
    /// a backup can be checked and unpacked without writing a decrypted copy to disk.
    /// </summary>
    /// <exception cref="BackupPassphraseException">The passphrase is wrong.</exception>
    public static Stream OpenDecrypted(string path, string passphrase)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        try
        {
            var access = Open(() => new AgeRandomAccess(file, new ScryptRecipient(passphrase)));
            return new OwningStream(access.GetStream(), access, file);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static T Open<T>(Func<T> open)
    {
        try
        {
            return open();
        }
        catch (Exception ex) when (ex is NoIdentityMatchException || ex.GetType().Name.Contains("Identity", StringComparison.Ordinal))
        {
            throw new BackupPassphraseException();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or IOException or BackupPassphraseException))
        {
            throw new InvalidDataException($"The backup could not be decrypted: {ex.Message}", ex);
        }
    }

    /// <summary>Reports bytes read and checks for cancellation between reads.</summary>
    private sealed class ProgressReadStream(Stream inner, Action<long> report, CancellationToken cancellationToken) : Stream
    {
        private long _read;

        public override int Read(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = inner.Read(buffer, offset, count);
            _read += read;
            report(_read);
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A plaintext stream that also disposes the decryptor and the file under it.</summary>
    private sealed class OwningStream(Stream inner, params IDisposable[] owned) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                foreach (var item in owned) item.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>The passphrase does not open this backup.</summary>
public sealed class BackupPassphraseException() : Exception("That passphrase does not open this backup.");
