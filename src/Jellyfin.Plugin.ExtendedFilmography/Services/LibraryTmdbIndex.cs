using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ExtendedFilmography.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExtendedFilmography.Services;

/// <summary>
/// Knows which TMDb titles are already in the library, so the plugin never offers the user
/// something they can already play.
/// </summary>
/// <remarks>
/// This is a read-only snapshot rebuilt at most once every few minutes. It never writes to the
/// library and never triggers a scan.
/// </remarks>
public sealed class LibraryTmdbIndex
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryTmdbIndex> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private HashSet<string> _keys = new(StringComparer.Ordinal);
    private DateTimeOffset _builtAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryTmdbIndex"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="logger">The logger.</param>
    public LibraryTmdbIndex(ILibraryManager libraryManager, ILogger<LibraryTmdbIndex> logger)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the current set of keys (see <see cref="FilmographyEntry.BuildKey"/>) for every
    /// film and series in the library that carries a TMDb id.
    /// </summary>
    /// <returns>The key set. Never null; empty if the library could not be read.</returns>
    public IReadOnlySet<string> GetKeys()
    {
        if (DateTimeOffset.UtcNow - _builtAt < RefreshInterval)
        {
            return _keys;
        }

        if (!_mutex.Wait(0))
        {
            _mutex.Wait();
            _mutex.Release();
            return _keys;
        }

        try
        {
            if (DateTimeOffset.UtcNow - _builtAt < RefreshInterval)
            {
                return _keys;
            }

            _keys = Build();
            _builtAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not index library TMDb ids; the de-duplication filter will be skipped");
        }
        finally
        {
            _mutex.Release();
        }

        return _keys;
    }

    /// <summary>
    /// Forces the next call to <see cref="GetKeys"/> to rebuild.
    /// </summary>
    public void Invalidate() => _builtAt = DateTimeOffset.MinValue;

    private HashSet<string> Build()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = new MediaBrowser.Controller.Dto.DtoOptions(true),
        };

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _libraryManager.GetItemList(query))
        {
            var kind = item switch
            {
                MediaBrowser.Controller.Entities.Movies.Movie => MediaKind.Movie,
                MediaBrowser.Controller.Entities.TV.Series => MediaKind.Series,
                _ => MediaKind.Unknown,
            };

            if (kind == MediaKind.Unknown)
            {
                continue;
            }

            if (!item.ProviderIds.TryGetValue("Tmdb", out var raw)
                || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId)
                || tmdbId <= 0)
            {
                continue;
            }

            keys.Add(FilmographyEntry.BuildKey(kind, tmdbId));
        }

        _logger.LogDebug("Indexed {Count} library titles with TMDb ids", keys.Count);
        return keys;
    }
}
