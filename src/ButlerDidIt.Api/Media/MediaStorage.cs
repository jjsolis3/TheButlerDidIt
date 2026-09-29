using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace ButlerDidIt.Api.Media;

/// <summary>
/// Where generated and uploaded files live. The database only stores each file's path
/// (MediaAsset.Path), so the store can change without touching anything else.
///
/// Browsers never reach the store directly: every file is served by /media/assets/{id},
/// so costume selfies stay behind the app even when the store is a public cloud bucket.
/// </summary>
public interface IMediaStore
{
    /// <summary>Saves the file and returns its path, like "2026-09/3f2a….png".</summary>
    Task<string> SaveAsync(byte[] bytes, string extension, string contentType, CancellationToken ct);

    /// <summary>The file's contents, or null if it isn't there. The stream is seekable, so audio can be served in ranges.</summary>
    Task<Stream?> OpenReadAsync(string path, CancellationToken ct);

    Task DeleteAsync(string path, CancellationToken ct);
}

/// <summary>Media URLs, whichever store holds the bytes.</summary>
public static class MediaStore
{
    public static string Url(Guid assetId) => $"/media/assets/{assetId}";

    /// <summary>The asset id inside one of our media URLs, or null for anything else (e.g. hand-made theme art).</summary>
    public static Guid? AssetIdFromUrl(string? url) =>
        url is not null && url.StartsWith("/media/assets/", StringComparison.Ordinal) && Guid.TryParse(url["/media/assets/".Length..], out var id) ? id : null;

    /// <summary>A new, unguessable path in a folder per month.</summary>
    internal static string NewPath(string extension) => $"{DateTime.UtcNow:yyyy-MM}/{Guid.NewGuid():N}.{extension}";
}

public sealed class MediaOptions
{
    /// <summary>"Local" (the default, a folder or Docker volume) or "S3" (any S3-compatible service).</summary>
    public string Storage { get; set; } = "Local";

    /// <summary>Folder for generated and uploaded files with Local storage. In Docker this is the `media` volume at /data/media.</summary>
    public string Root { get; set; } = "data/media";

    public S3MediaOptions S3 { get; set; } = new();
}

public sealed class S3MediaOptions
{
    /// <summary>Endpoint for anything that isn't AWS itself, e.g. https://&lt;account&gt;.r2.cloudflarestorage.com or http://minio:9000. Empty for AWS.</summary>
    public string? ServiceUrl { get; set; }
    public string Region { get; set; } = "auto";
    public string Bucket { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
}

/// <summary>Files in a folder on this server (or a Docker volume). Only one server can use it.</summary>
public sealed class LocalMediaStore(IOptions<MediaOptions> options, IWebHostEnvironment env) : IMediaStore
{
    private string Root => Path.GetFullPath(Path.Combine(env.ContentRootPath, options.Value.Root));

    public async Task<string> SaveAsync(byte[] bytes, string extension, string contentType, CancellationToken ct)
    {
        var relative = MediaStore.NewPath(extension);
        var full = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes, ct);
        return relative;
    }

    public Task<Stream?> OpenReadAsync(string path, CancellationToken ct) =>
        Task.FromResult<Stream?>(Resolve(path) is { } full ? File.OpenRead(full) : null);

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        if (Resolve(path) is { } full) File.Delete(full);
        return Task.CompletedTask;
    }

    /// <summary>Resolves a stored relative path, refusing anything that escapes the media folder.</summary>
    private string? Resolve(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relative));
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full) ? full : null;
    }
}

/// <summary>
/// Files in an S3-compatible bucket: AWS S3, Cloudflare R2, Backblaze B2, MinIO… Every
/// server sees the same files, so this is the store to use with more than one server.
/// </summary>
public sealed class S3MediaStore : IMediaStore, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;

    public S3MediaStore(IOptions<MediaOptions> options)
    {
        var o = options.Value.S3;
        if (string.IsNullOrWhiteSpace(o.Bucket)) throw new InvalidOperationException("Set Media__S3__Bucket to use S3 media storage.");
        _bucket = o.Bucket;
        var config = new AmazonS3Config
        {
            // Most S3-compatible services want https://host/bucket/key rather than https://bucket.host/key.
            ForcePathStyle = true,
            // Only send checksums when a request needs one: R2 and older MinIO reject the newer default ones.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (!string.IsNullOrWhiteSpace(o.ServiceUrl))
        {
            config.ServiceURL = o.ServiceUrl;
            config.AuthenticationRegion = o.Region;
        }
        else
        {
            config.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(o.Region);
        }
        _client = new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), config);
    }

    public async Task<string> SaveAsync(byte[] bytes, string extension, string contentType, CancellationToken ct)
    {
        var key = MediaStore.NewPath(extension);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket, Key = key, ContentType = contentType, InputStream = new MemoryStream(bytes),
        }, ct);
        return key;
    }

    public async Task<Stream?> OpenReadAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _client.GetObjectAsync(_bucket, path, ct);
            // Files are small (a picture or a voice clip), so buffer them: the copy is seekable,
            // which lets the endpoint serve byte ranges for audio scrubbing.
            var copy = new MemoryStream();
            await response.ResponseStream.CopyToAsync(copy, ct);
            copy.Position = 0;
            return copy;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string path, CancellationToken ct) =>
        await _client.DeleteObjectAsync(_bucket, path, ct);

    public void Dispose() => _client.Dispose();
}
