using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using TradeFoundry.Core;

namespace TradeFoundry.Services.Backup;

/// <summary>An object in the bucket: the newest real version of a key, hidden when a newer hide marker covers it.</summary>
public sealed record CloudObject(string Key, long Size, string? VersionId, DateTimeOffset LastModified, bool Hidden);

/// <summary>The few bucket operations backups need. B2 implements them; tests use an in-memory bucket.</summary>
public interface IBackupCloud : IDisposable
{
    Task PutFileAsync(string key, string path, IProgress<double>? progress, CancellationToken cancellationToken);
    Task PutBytesAsync(string key, byte[] content, CancellationToken cancellationToken);
    /// <summary>Lists keys under <paramref name="prefix"/>. With <paramref name="includeHidden"/>, also hidden keys still in the recovery window.</summary>
    Task<IReadOnlyList<CloudObject>> ListAsync(string prefix, bool includeHidden, CancellationToken cancellationToken);
    Task<byte[]> ReadBytesAsync(string key, string? versionId, long maxBytes, CancellationToken cancellationToken);
    Task DownloadAsync(string key, string? versionId, string path, IProgress<double>? progress, CancellationToken cancellationToken);
    /// <summary>Hides a key (a delete without a version id). B2 erases it after the bucket's lifecycle window.</summary>
    Task HideAsync(string key, CancellationToken cancellationToken);
    /// <summary>Tries to erase one version for good. Returns false when the key is refused, which is what a correctly restricted key does.</summary>
    Task<bool> TryDeleteVersionAsync(string key, string versionId, CancellationToken cancellationToken);
    /// <summary>Cancels unfinished multipart uploads under <paramref name="prefix"/>, left by a crash.</summary>
    Task AbortUnfinishedAsync(string prefix, CancellationToken cancellationToken);
}

public interface IBackupCloudFactory
{
    IBackupCloud Create(B2Connection connection);
}

public sealed class B2CloudFactory : IBackupCloudFactory
{
    public IBackupCloud Create(B2Connection connection) => new B2Cloud(connection);
}

/// <summary>Backblaze B2 through its S3-compatible API.</summary>
public sealed partial class B2Cloud : IBackupCloud
{
    public const int PartSize = 16 * 1024 * 1024;
    private readonly AmazonS3Client _client;
    private readonly string _bucket;

    [GeneratedRegex(@"^s3\.([a-z0-9-]+)\.backblazeb2\.com$")]
    private static partial Regex EndpointHost();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{4,61}[a-z0-9]$")]
    private static partial Regex BucketName();

    public B2Cloud(B2Connection connection)
    {
        var endpoint = NormalizeEndpoint(connection.Endpoint) ?? throw new ArgumentException("Not a Backblaze B2 S3 endpoint.", nameof(connection));
        _bucket = connection.Bucket;
        _client = new AmazonS3Client(new BasicAWSCredentials(connection.KeyId, connection.ApplicationKey), new AmazonS3Config
        {
            ServiceURL = endpoint,
            AuthenticationRegion = EndpointHost().Match(new Uri(endpoint).Host).Groups[1].Value,
            ForcePathStyle = true,
            // B2 has rejected the SDK's default flexible checksums; Content-MD5 and our own SHA-256 are used instead.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromMinutes(5),
            MaxErrorRetry = 4
        });
    }

    /// <summary>Returns the endpoint as <c>https://s3.&lt;region&gt;.backblazeb2.com</c>, or null when it is not one.</summary>
    public static string? NormalizeEndpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        if (uri.AbsolutePath is not ("/" or "") || !string.IsNullOrEmpty(uri.Query) || !uri.IsDefaultPort) return null;
        var host = uri.Host.ToLowerInvariant();
        return EndpointHost().IsMatch(host) ? $"https://{host}" : null;
    }

    public static bool IsValidBucket(string? bucket) => bucket is not null && BucketName().IsMatch(bucket);

    public async Task PutFileAsync(string key, string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;
        if (length <= PartSize)
        {
            await PutBytesAsync(key, await File.ReadAllBytesAsync(path, cancellationToken), cancellationToken);
            progress?.Report(1);
            return;
        }

        var upload = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = _bucket, Key = key }, cancellationToken);
        try
        {
            var parts = new List<PartETag>();
            var buffer = new byte[PartSize];
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long sent = 0;
            for (var number = 1; sent < length; number++)
            {
                var count = await input.ReadAtLeastAsync(buffer, (int)Math.Min(PartSize, length - sent), throwOnEndOfStream: true, cancellationToken);
                var response = await _client.UploadPartAsync(new UploadPartRequest
                {
                    BucketName = _bucket,
                    Key = key,
                    UploadId = upload.UploadId,
                    PartNumber = number,
                    InputStream = new MemoryStream(buffer, 0, count, writable: false),
                    PartSize = count,
                    MD5Digest = Convert.ToBase64String(MD5.HashData(buffer.AsSpan(0, count)))
                }, cancellationToken);
                parts.Add(new PartETag(number, response.ETag));
                sent += count;
                progress?.Report((double)sent / length);
            }

            await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = _bucket, Key = key, UploadId = upload.UploadId, PartETags = parts }, cancellationToken);
        }
        catch
        {
            // An aborted upload leaves nothing listed; writeFiles is enough to abort.
            try
            {
                await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = _bucket, Key = key, UploadId = upload.UploadId }, CancellationToken.None);
            }
            catch (Exception ex) when (ex is AmazonServiceException or HttpRequestException or IOException)
            {
            }

            throw;
        }
    }

    public async Task PutBytesAsync(string key, byte[] content, CancellationToken cancellationToken) =>
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = new MemoryStream(content, writable: false),
            MD5Digest = Convert.ToBase64String(MD5.HashData(content))
        }, cancellationToken);

    public async Task<IReadOnlyList<CloudObject>> ListAsync(string prefix, bool includeHidden, CancellationToken cancellationToken)
    {
        var objects = new List<CloudObject>();
        if (!includeHidden)
        {
            string? token = null;
            do
            {
                var page = await _client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, ContinuationToken = token }, cancellationToken);
                foreach (var item in page.S3Objects ?? [])
                    objects.Add(new CloudObject(item.Key, item.Size ?? 0, null, item.LastModified is { } modified ? new DateTimeOffset(modified.ToUniversalTime()) : DateTimeOffset.MinValue, false));
                token = page.IsTruncated == true ? page.NextContinuationToken : null;
            }
            while (token is not null);
            return objects;
        }

        // Versions come newest first per key. A key is hidden when its newest entry is a hide marker.
        var versions = new List<S3ObjectVersion>();
        string? keyMarker = null, versionMarker = null;
        do
        {
            var page = await _client.ListVersionsAsync(new ListVersionsRequest { BucketName = _bucket, Prefix = prefix, KeyMarker = keyMarker, VersionIdMarker = versionMarker }, cancellationToken);
            versions.AddRange(page.Versions ?? []);
            (keyMarker, versionMarker) = page.IsTruncated == true ? (page.NextKeyMarker, page.NextVersionIdMarker) : (null, null);
        }
        while (keyMarker is not null);

        foreach (var group in versions.GroupBy(version => version.Key, StringComparer.Ordinal))
        {
            var ordered = group.OrderByDescending(version => version.LastModified).ToList();
            var newestFile = ordered.FirstOrDefault(version => version.IsDeleteMarker != true);
            if (newestFile is null) continue;
            var hidden = ordered[0].IsDeleteMarker == true;
            objects.Add(new CloudObject(newestFile.Key, newestFile.Size ?? 0, newestFile.VersionId,
                newestFile.LastModified is { } modified ? new DateTimeOffset(modified.ToUniversalTime()) : DateTimeOffset.MinValue, hidden));
        }

        return objects;
    }

    public async Task<byte[]> ReadBytesAsync(string key, string? versionId, long maxBytes, CancellationToken cancellationToken)
    {
        using var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key, VersionId = versionId }, cancellationToken);
        if (response.ContentLength > maxBytes) throw new InvalidDataException($"{key} is larger than expected.");
        using var copy = new MemoryStream();
        await response.ResponseStream.CopyToAsync(copy, cancellationToken);
        return copy.ToArray();
    }

    public async Task DownloadAsync(string key, string? versionId, string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key, VersionId = versionId }, cancellationToken);
        var total = Math.Max(1, response.ContentLength);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
        var buffer = new byte[1024 * 1024];
        long done = 0;
        int read;
        while ((read = await response.ResponseStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            done += read;
            progress?.Report((double)done / total);
        }
    }

    public async Task HideAsync(string key, CancellationToken cancellationToken) =>
        await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, cancellationToken);

    public async Task<bool> TryDeleteVersionAsync(string key, string versionId, CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key, VersionId = versionId }, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return false;
        }
    }

    public async Task AbortUnfinishedAsync(string prefix, CancellationToken cancellationToken)
    {
        var uploads = await _client.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = _bucket, Prefix = prefix }, cancellationToken);
        foreach (var upload in uploads.MultipartUploads ?? [])
            await _client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = _bucket, Key = upload.Key, UploadId = upload.UploadId }, cancellationToken);
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Turns an SDK failure into a sentence the owner can act on.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        AmazonS3Exception { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized } s3 when s3.ErrorCode is "InvalidAccessKeyId" or "SignatureDoesNotMatch" =>
            "Backblaze B2 did not accept the key ID and application key.",
        AmazonS3Exception { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized } s3 =>
            $"Backblaze B2 refused the request ({s3.ErrorCode}). Check that the key is for this bucket and allows the tradefoundry/ prefix.",
        AmazonS3Exception { ErrorCode: "NoSuchBucket" } => "Backblaze B2 has no bucket with that name for this key.",
        AmazonS3Exception s3 => $"Backblaze B2 returned an error: {s3.ErrorCode} {s3.Message}",
        HttpRequestException or AmazonClientException => $"Backblaze B2 could not be reached: {exception.Message}",
        _ => exception.Message
    };
}
