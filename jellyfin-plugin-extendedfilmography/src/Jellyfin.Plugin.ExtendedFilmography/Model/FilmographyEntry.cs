using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ExtendedFilmography.Model;

/// <summary>
/// A single ranked, filtered credit that will be surfaced as a synthetic Jellyfin item.
/// This is the plugin's own model: it is what gets cached to disk, and it is deliberately
/// decoupled from both TMDb's wire format and Jellyfin's DTO types.
/// </summary>
public sealed class FilmographyEntry
{
    /// <summary>Gets or sets the TMDb id of the title.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets whether this is a film or a series.</summary>
    public MediaKind Kind { get; set; }

    /// <summary>Gets or sets the display title.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the original-language title, when it differs.</summary>
    public string? OriginalTitle { get; set; }

    /// <summary>Gets or sets the synopsis.</summary>
    public string? Overview { get; set; }

    /// <summary>Gets or sets the TMDb poster path, e.g. "/abc123.jpg".</summary>
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the TMDb backdrop path.</summary>
    public string? BackdropPath { get; set; }

    /// <summary>Gets or sets the release / first-air date.</summary>
    public DateTime? PremiereDate { get; set; }

    /// <summary>Gets or sets the TMDb community rating out of 10.</summary>
    public double? CommunityRating { get; set; }

    /// <summary>Gets or sets the number of TMDb votes behind <see cref="CommunityRating"/>.</summary>
    public int VoteCount { get; set; }

    /// <summary>Gets or sets the TMDb popularity metric.</summary>
    public double Popularity { get; set; }

    /// <summary>Gets or sets the resolved genre names.</summary>
    public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the character the person played.</summary>
    public string? Character { get; set; }

    /// <summary>Gets or sets the computed rank score. Higher is better.</summary>
    public double Score { get; set; }

    /// <summary>
    /// Gets a stable cache/dedupe key, e.g. "movie:603".
    /// </summary>
    public string Key => BuildKey(Kind, TmdbId);

    /// <summary>
    /// Builds the stable cache/dedupe key for a kind and TMDb id.
    /// </summary>
    /// <param name="kind">The media kind.</param>
    /// <param name="tmdbId">The TMDb id.</param>
    /// <returns>A key such as "movie:603".</returns>
    public static string BuildKey(MediaKind kind, int tmdbId)
        => (kind == MediaKind.Movie ? "movie:" : "tv:") + tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
