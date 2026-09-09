using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ExtendedFilmography.Model;

namespace Jellyfin.Plugin.ExtendedFilmography.Services;

/// <summary>
/// Converts a TMDb title detail response into a filmography entry, for the fallback path where
/// a detail page is requested for something that is no longer in the cache.
/// </summary>
public static class TitleConverter
{
    /// <summary>
    /// Converts a TMDb title.
    /// </summary>
    /// <param name="title">The TMDb payload.</param>
    /// <param name="kind">Film or series.</param>
    /// <returns>The entry, or <c>null</c> if the payload was unusable.</returns>
    public static FilmographyEntry? Convert(TmdbTitle? title, MediaKind kind)
    {
        if (title is null || title.Id <= 0)
        {
            return null;
        }

        var name = title.DisplayName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var original = title.DisplayOriginalName;

        return new FilmographyEntry
        {
            TmdbId = title.Id,
            Kind = kind,
            Name = name,
            OriginalTitle = string.Equals(original, name, StringComparison.Ordinal) ? null : original,
            Overview = string.IsNullOrWhiteSpace(title.Overview) ? null : title.Overview,
            PosterPath = title.PosterPath,
            BackdropPath = title.BackdropPath,
            PremiereDate = ParseDate(title.DisplayDate),
            CommunityRating = title.VoteAverage > 0 ? Math.Round(title.VoteAverage, 1) : null,
            VoteCount = title.VoteCount,
            Popularity = title.Popularity,
            Genres = ResolveGenres(title.Genres),
            Score = 0,
        };
    }

    private static IReadOnlyList<string> ResolveGenres(List<TmdbGenre>? genres)
    {
        if (genres is null || genres.Count == 0)
        {
            return Array.Empty<string>();
        }

        return genres
            .Select(g => g.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }
}
