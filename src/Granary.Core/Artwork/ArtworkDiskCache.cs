using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Granary.Core.Artwork;

/// <summary>
/// Images on disk, one file per cache key, up to a budget of bytes. Past the budget the least recently used
/// files go until a fifth of it is free again; a file's write time records its last use. The cache is best
/// effort: a failing read or write is logged and counts as a miss.
/// </summary>
public sealed class ArtworkDiskCache
{
    public const long DefaultCapacityBytes = 512L * 1024 * 1024;

    // A read marks its file as used only when the mark is older than this, so browsing does not rewrite
    // the timestamps of the same files over and over.
    private static readonly TimeSpan UseResolution = TimeSpan.FromDays(1);

    private readonly string _directory;
    private readonly long _capacityBytes;
    private readonly ILogger<ArtworkDiskCache> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    // Unknown (-1) until the first write counts the files that are already there.
    private long _size = -1;

    public ArtworkDiskCache(string directory, long capacityBytes, ILogger<ArtworkDiskCache> logger, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        _directory = Path.GetFullPath(directory);
        _capacityBytes = capacityBytes;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public static string DefaultDirectory => Path.Combine(AppPaths.DataDirectory, "cache", "artwork");

    public bool Contains(string key) => File.Exists(PathFor(key));

    public async Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        string path = PathFor(key);
        try
        {
            var data = await File.ReadAllBytesAsync(path, cancellationToken);
            var now = _time.GetUtcNow().UtcDateTime;
            if (now - File.GetLastWriteTimeUtc(path) > UseResolution)
            {
                File.SetLastWriteTimeUtc(path, now);
            }
            return data;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not read cached artwork: {Failure}", ex.Message);
            return null;
        }
    }

    public async Task WriteAsync(string key, byte[] data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        string path = PathFor(key);
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(_directory);
            long replaced = File.Exists(path) ? new FileInfo(path).Length : 0;
            await File.WriteAllBytesAsync(temporary, data, cancellationToken);
            File.Move(temporary, path, overwrite: true);
            File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime);
            Account(data.Length - replaced);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            TryDelete(temporary);
            if (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Could not cache artwork: {Failure}", ex.Message);
            }
        }
    }

    public void Remove(string key)
    {
        string path = PathFor(key);
        if (File.Exists(path))
        {
            long length = new FileInfo(path).Length;
            if (TryDelete(path))
            {
                Account(-length);
            }
        }
    }

    /// <summary>The file of <paramref name="key"/>: its SHA-256, so keys need no escaping and reveal nothing.</summary>
    internal string PathFor(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
    }

    private void Account(long change)
    {
        lock (_gate)
        {
            _size = _size < 0 ? CountFiles() : Math.Max(0, _size + change);
            if (_size > _capacityBytes)
            {
                Trim();
            }
        }
    }

    // Called under the lock.
    private void Trim()
    {
        long target = _capacityBytes * 4 / 5;
        var files = Files().OrderBy(f => f.LastWriteTimeUtc).ToList();
        long size = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (size <= target)
            {
                break;
            }
            if (TryDelete(file.FullName))
            {
                size -= file.Length;
            }
        }
        _size = size;
    }

    private long CountFiles() => Files().Sum(f => f.Length);

    // Cached images only: their names have no extension, unlike temporary files of writes in progress.
    private IEnumerable<FileInfo> Files() =>
        Directory.Exists(_directory)
            ? new DirectoryInfo(_directory).EnumerateFiles().Where(f => f.Extension.Length == 0)
            : [];

    private bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not delete cached artwork: {Failure}", ex.Message);
            return false;
        }
    }
}
