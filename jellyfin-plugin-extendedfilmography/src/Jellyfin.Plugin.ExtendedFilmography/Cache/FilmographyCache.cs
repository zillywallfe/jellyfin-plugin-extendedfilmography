using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExtendedFilmography.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExtendedFilmography.Cache;

/// <summary>
/// One person's ranked filmography, as stored on disk.
/// </summary>
public sealed class CachedFilmography
{
    /// <summary>Gets or sets the TMDb person id.</summary>
    [JsonPropertyName("personTmdbId")]
    public int PersonTmdbId { get; set; }

    /// <summary>Gets or sets when the entry was written.</summary>
    [JsonPropertyName("fetchedAt")]
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>
    /// Gets or sets a hash of the settings that affect selection, so changing a filter
    /// invalidates the cache without the user having to clear anything.
    /// </summary>
    [JsonPropertyName("settingsStamp")]
    public string SettingsStamp { get; set; } = string.Empty;

    /// <summary>Gets or sets the ranked entries.</summary>
    [JsonPropertyName("entries")]
    public List<FilmographyEntry> Entries { get; set; } = new();
}

/// <summary>
/// A two-tier cache: a bounded in-memory dictionary in front of one small JSON file per person
/// in the plugin's own data directory.
/// </summary>
/// <remarks>
/// This is the plugin's entire disk footprint. Roughly 4 KB per person actually visited, so a
/// library whose cast pages have all been browsed costs a couple of megabytes. Nothing is ever
/// written to Jellyfin's library database, and no images are stored.
/// </remarks>
public sealed class FilmographyCache
{
    private const int MaxInMemoryEntries = 500;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly Dictionary<int, CachedFilmography> _memory = new();
    private readonly LinkedList<int> _recency = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="FilmographyCache"/> class.
    /// </summary>
    /// <param name="directory">The directory to store cache files in.</param>
    /// <param name="logger">The logger.</param>
    public FilmographyCache(string directory, ILogger logger)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Reads a person's cached filmography if it is present and still valid.
    /// </summary>
    /// <param name="personTmdbId">The TMDb person id.</param>
    /// <param name="settingsStamp">The current settings stamp.</param>
    /// <param name="ttl">How long an entry stays valid.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached entries, or <c>null</c> on a miss.</returns>
    public async Task<IReadOnlyList<FilmographyEntry>?> TryGetAsync(
        int personTmdbId,
        string settingsStamp,
        TimeSpan ttl,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_memory.TryGetValue(personTmdbId, out var hot))
            {
                if (IsValid(hot, settingsStamp, ttl))
                {
                    Touch(personTmdbId);
                    return hot.Entries;
                }

                Evict(personTmdbId);
            }
        }
        finally
        {
            _mutex.Release();
        }

        var cold = await ReadFileAsync(personTmdbId, cancellationToken).ConfigureAwait(false);
        if (cold is null || !IsValid(cold, settingsStamp, ttl))
        {
            return null;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Store(personTmdbId, cold);
        }
        finally
        {
            _mutex.Release();
        }

        return cold.Entries;
    }

    /// <summary>
    /// Writes a person's filmography to both tiers.
    /// </summary>
    /// <param name="personTmdbId">The TMDb person id.</param>
    /// <param name="settingsStamp">The current settings stamp.</param>
    /// <param name="entries">The ranked entries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SetAsync(
        int personTmdbId,
        string settingsStamp,
        IReadOnlyList<FilmographyEntry> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var record = new CachedFilmography
        {
            PersonTmdbId = personTmdbId,
            FetchedAt = DateTimeOffset.UtcNow,
            SettingsStamp = settingsStamp,
            Entries = new List<FilmographyEntry>(entries),
        };

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Store(personTmdbId, record);
        }
        finally
        {
            _mutex.Release();
        }

        await WriteFileAsync(personTmdbId, record, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes every cached file and clears the in-memory tier.
    /// </summary>
    /// <returns>The number of files deleted.</returns>
    public int Clear()
    {
        _mutex.Wait();
        try
        {
            _memory.Clear();
            _recency.Clear();
        }
        finally
        {
            _mutex.Release();
        }

        var deleted = 0;
        try
        {
            if (!Directory.Exists(_directory))
            {
                return 0;
            }

            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (IOException)
                {
                    // Another request is reading it; it will expire on its own.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not fully clear the filmography cache");
        }

        return deleted;
    }

    /// <summary>
    /// Reports how much disk the cache is using, for the settings page.
    /// </summary>
    /// <returns>A tuple of file count and total bytes.</returns>
    public (int Files, long Bytes) Usage()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return (0, 0);
            }

            var files = 0;
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                files++;
                bytes += new FileInfo(file).Length;
            }

            return (files, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not measure the filmography cache");
            return (0, 0);
        }
    }

    private static bool IsValid(CachedFilmography record, string settingsStamp, TimeSpan ttl)
        => string.Equals(record.SettingsStamp, settingsStamp, StringComparison.Ordinal)
           && DateTimeOffset.UtcNow - record.FetchedAt < ttl;

    private string PathFor(int personTmdbId)
        => Path.Combine(_directory, personTmdbId.ToString(CultureInfo.InvariantCulture) + ".json");

    private async Task<CachedFilmography?> ReadFileAsync(int personTmdbId, CancellationToken cancellationToken)
    {
        var path = PathFor(personTmdbId);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            await using var stream = File.OpenRead(path);
            return await JsonSerializer
                .DeserializeAsync<CachedFilmography>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Discarding unreadable cache file for person {PersonId}", personTmdbId);
            return null;
        }
    }

    private async Task WriteFileAsync(int personTmdbId, CachedFilmography record, CancellationToken cancellationToken)
    {
        var path = PathFor(personTmdbId);
        var temp = path + ".tmp";

        try
        {
            Directory.CreateDirectory(_directory);

            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, record, SerializerOptions, cancellationToken).ConfigureAwait(false);
            }

            // Atomic replace, so a crash mid-write cannot leave a half-parsed file behind.
            File.Move(temp, path, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not cache filmography for person {PersonId}", personTmdbId);
            TryDelete(temp);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remove temporary cache file {Path}", path);
        }
    }

    private void Store(int personTmdbId, CachedFilmography record)
    {
        _memory[personTmdbId] = record;
        Touch(personTmdbId);

        while (_memory.Count > MaxInMemoryEntries && _recency.Last is not null)
        {
            Evict(_recency.Last.Value);
        }
    }

    private void Touch(int personTmdbId)
    {
        _recency.Remove(personTmdbId);
        _recency.AddFirst(personTmdbId);
    }

    private void Evict(int personTmdbId)
    {
        _memory.Remove(personTmdbId);
        _recency.Remove(personTmdbId);
    }
}
