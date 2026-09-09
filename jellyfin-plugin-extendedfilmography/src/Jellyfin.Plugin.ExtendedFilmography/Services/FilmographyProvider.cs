using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExtendedFilmography.Abstractions;
using Jellyfin.Plugin.ExtendedFilmography.Cache;
using Jellyfin.Plugin.ExtendedFilmography.Configuration;
using Jellyfin.Plugin.ExtendedFilmography.Model;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExtendedFilmography.Services;

/// <summary>
/// Ties the pieces together: resolves a Jellyfin person to a TMDb person, fetches and ranks
/// their credits, and keeps the result cached.
/// </summary>
public sealed class FilmographyProvider : IFilmographyProvider, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationHost _appHost;
    private readonly TmdbClient _tmdb;
    private readonly FilmographyCache _cache;
    private readonly LibraryTmdbIndex _libraryIndex;
    private readonly ILogger<FilmographyProvider> _logger;

    /// <summary>
    /// In-flight lookups, keyed by TMDb person id. A person page fires several item queries at
    /// once; without this they would each start their own TMDb request.
    /// </summary>
    private readonly ConcurrentDictionary<int, Task<IReadOnlyList<FilmographyEntry>>> _inFlight = new();

    /// <summary>
    /// Every entry the plugin has handed out this server lifetime, keyed by
    /// <see cref="FilmographyEntry.Key"/>. This is what answers a detail-page request without a
    /// second TMDb round trip, since a detail page is always reached from a person page.
    /// </summary>
    private readonly ConcurrentDictionary<string, FilmographyEntry> _entryIndex = new(StringComparer.Ordinal);

    private bool _disposed;

    /// <summary>Ceiling on the entry index, so a huge library cannot grow it without bound.</summary>
    private const int MaxIndexedEntries = 20_000;

    /// <summary>
    /// Initializes a new instance of the <see cref="FilmographyProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin's library manager.</param>
    /// <param name="appHost">The server application host, for the server id.</param>
    /// <param name="tmdb">The TMDb client.</param>
    /// <param name="cache">The filmography cache.</param>
    /// <param name="libraryIndex">The index of TMDb ids already in the library.</param>
    /// <param name="logger">The logger.</param>
    public FilmographyProvider(
        ILibraryManager libraryManager,
        IServerApplicationHost appHost,
        TmdbClient tmdb,
        FilmographyCache cache,
        LibraryTmdbIndex libraryIndex,
        ILogger<FilmographyProvider> logger)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _appHost = appHost ?? throw new ArgumentNullException(nameof(appHost));
        _tmdb = tmdb ?? throw new ArgumentNullException(nameof(tmdb));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _libraryIndex = libraryIndex ?? throw new ArgumentNullException(nameof(libraryIndex));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public PluginConfiguration Configuration => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public string ServerId => _appHost.SystemId;

    /// <inheritdoc />
    public async Task<IReadOnlyList<FilmographyEntry>> GetForPersonAsync(Guid personId, CancellationToken cancellationToken)
    {
        var config = Configuration;

        if (!config.Enabled || string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            return Array.Empty<FilmographyEntry>();
        }

        var personTmdbId = ResolvePersonTmdbId(personId);
        if (personTmdbId is null)
        {
            return Array.Empty<FilmographyEntry>();
        }

        var stamp = BuildSettingsStamp(config);
        var ttl = TimeSpan.FromDays(Math.Max(1, config.CacheTtlDays));

        var cached = await _cache.TryGetAsync(personTmdbId.Value, stamp, ttl, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return SelectForLibrary(cached, config);
        }

        // Cold. Start (or join) the lookup, but do not let it hold up the page indefinitely:
        // on timeout the request finishes in the background and populates the cache, so the
        // next visit to this person is instant.
        var lookup = _inFlight.GetOrAdd(
            personTmdbId.Value,
            static (id, state) => state.Self.LoadAsync(id, state.Config, state.Stamp),
            (Self: this, Config: config, Stamp: stamp));

        var budget = Math.Max(250, config.LookupTimeoutMs);

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timer = Task.Delay(budget, budgetCts.Token);
        var completed = await Task.WhenAny(lookup, timer).ConfigureAwait(false);

        // Stop the timer as soon as the lookup wins, so a busy person page does not leave one
        // pending timer per request for the length of the budget.
        budgetCts.Cancel();

        if (!ReferenceEquals(completed, lookup))
        {
            if (config.VerboseLogging)
            {
                _logger.LogDebug(
                    "TMDb lookup for person {PersonId} exceeded the {Budget}ms budget; continuing in the background",
                    personTmdbId.Value,
                    budget);
            }

            return Array.Empty<FilmographyEntry>();
        }

        return SelectForLibrary(await lookup.ConfigureAwait(false), config);
    }

    /// <inheritdoc />
    public async Task<FilmographyEntry?> GetEntryAsync(MediaKind kind, int tmdbId, CancellationToken cancellationToken)
    {
        if (kind == MediaKind.Unknown || tmdbId <= 0)
        {
            return null;
        }

        var key = FilmographyEntry.BuildKey(kind, tmdbId);
        if (_entryIndex.TryGetValue(key, out var known))
        {
            return known;
        }

        // Not indexed. This happens when a client kept an id across a server restart. Fetch the
        // one title directly rather than show a broken page.
        var config = Configuration;
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            return null;
        }

        var title = await _tmdb
            .GetTitleAsync(kind, tmdbId, config.TmdbApiKey, config.TmdbLanguage, cancellationToken)
            .ConfigureAwait(false);

        var entry = TitleConverter.Convert(title, kind);
        if (entry is not null)
        {
            Index(new[] { entry });
        }

        return entry;
    }

    private IReadOnlyList<FilmographyEntry> SelectForLibrary(
        IReadOnlyList<FilmographyEntry> candidates, PluginConfiguration config)
    {
        var keys = config.HideItemsAlreadyInLibrary ? _libraryIndex.GetKeys() : null;
        var selected = CreditRanker.SelectForLibrary(candidates, keys, config.MaxItems);
        Index(selected);
        return selected;
    }

    private void Index(IReadOnlyList<FilmographyEntry> entries)
    {
        if (_entryIndex.Count > MaxIndexedEntries)
        {
            _entryIndex.Clear();
        }

        foreach (var entry in entries)
        {
            _entryIndex[entry.Key] = entry;
        }
    }

    /// <summary>
    /// Builds a short stamp of every setting that changes which titles are selected. When any
    /// of them changes, cached results stop matching and are refetched.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The stamp.</returns>
    public static string BuildSettingsStamp(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var builder = new StringBuilder(96);
        builder.Append("candidates-v2|").Append(config.MaxItems).Append('|')
            .Append(config.PopularityWeight.ToString("F3", CultureInfo.InvariantCulture)).Append('|')
            .Append(config.RatingWeight.ToString("F3", CultureInfo.InvariantCulture)).Append('|')
            .Append(config.MinVoteCount).Append('|')
            .Append(config.MaxBillingOrder).Append('|')
            .Append(config.MinEpisodeCountWithoutOrder).Append('|')
            .Append(config.IncludeMovies ? '1' : '0')
            .Append(config.IncludeSeries ? '1' : '0')
            .Append(config.ActingCreditsOnly ? '1' : '0')
            .Append(config.ExcludeSelfAppearances ? '1' : '0')
            .Append(config.ExcludeTalkNewsReality ? '1' : '0')
            .Append(config.HideItemsAlreadyInLibrary ? '1' : '0')
            .Append(config.ExcludeUnreleased ? '1' : '0')
            .Append('|')
            .Append(config.TmdbLanguage);

        return builder.ToString();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _tmdb.Dispose();
        _disposed = true;
    }

    private async Task<IReadOnlyList<FilmographyEntry>> LoadAsync(int personTmdbId, PluginConfiguration config, string stamp)
    {
        try
        {
            // Deliberately not tied to the triggering request's cancellation token: if the
            // person page times out and gives up, we still want the result cached.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var credits = await _tmdb
                .GetCombinedCreditsAsync(personTmdbId, config.TmdbApiKey, config.TmdbLanguage, timeout.Token)
                .ConfigureAwait(false);

            if (credits is null)
            {
                return Array.Empty<FilmographyEntry>();
            }

            // Cache all eligible credits, independent of the changing local library.
            var ranked = CreditRanker.Rank(credits, config, null, DateTime.UtcNow, limitResults: false);

            await _cache.SetAsync(personTmdbId, stamp, ranked, timeout.Token).ConfigureAwait(false);
            Index(ranked);

            if (config.VerboseLogging)
            {
                _logger.LogDebug(
                    "Ranked {Kept} of {Total} credits for TMDb person {PersonId}",
                    ranked.Count,
                    credits.Cast.Count + credits.Crew.Count,
                    personTmdbId);
            }

            return ranked;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Filmography load failed for TMDb person {PersonId}", personTmdbId);
            return Array.Empty<FilmographyEntry>();
        }
        finally
        {
            _inFlight.TryRemove(personTmdbId, out _);
        }
    }

    private int? ResolvePersonTmdbId(Guid personId)
    {
        try
        {
            var item = _libraryManager.GetItemById(personId);
            if (item is null)
            {
                return null;
            }

            if (!item.ProviderIds.TryGetValue("Tmdb", out var raw)
                || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId)
                || tmdbId <= 0)
            {
                return null;
            }

            return tmdbId;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve person {PersonId}", personId);
            return null;
        }
    }
}
