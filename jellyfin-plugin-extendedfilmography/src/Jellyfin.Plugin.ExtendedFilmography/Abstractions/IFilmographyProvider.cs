using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExtendedFilmography.Configuration;
using Jellyfin.Plugin.ExtendedFilmography.Model;

namespace Jellyfin.Plugin.ExtendedFilmography.Abstractions;

/// <summary>
/// Everything the middleware needs from the rest of the plugin.
/// </summary>
/// <remarks>
/// Keeping this narrow is deliberate: the middleware is the fiddliest part of the plugin, and
/// this interface is what lets it be exercised end-to-end without a Jellyfin server behind it.
/// </remarks>
public interface IFilmographyProvider
{
    /// <summary>Gets the current plugin settings.</summary>
    PluginConfiguration Configuration { get; }

    /// <summary>Gets the Jellyfin server id, which clients expect on every item.</summary>
    string ServerId { get; }

    /// <summary>
    /// Gets the ranked external filmography for a person.
    /// </summary>
    /// <param name="personId">The Jellyfin item id of the person.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The entries to append, newest lookup first. Returns an empty list rather than throwing
    /// when the person has no TMDb id, when TMDb is unreachable, or when a cold lookup did not
    /// finish inside the configured budget.
    /// </returns>
    Task<IReadOnlyList<FilmographyEntry>> GetForPersonAsync(Guid personId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a single entry by its TMDb identity, for rendering a detail page.
    /// </summary>
    /// <param name="kind">Film or series.</param>
    /// <param name="tmdbId">The TMDb id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The entry, or <c>null</c> if it is not known.</returns>
    Task<FilmographyEntry?> GetEntryAsync(MediaKind kind, int tmdbId, CancellationToken cancellationToken);
}
