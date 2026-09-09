using System.Collections.Generic;
using System.Collections.Immutable;
using Jellyfin.Plugin.ExtendedFilmography.Model;

namespace Jellyfin.Plugin.ExtendedFilmography.Services;

/// <summary>
/// TMDb genre id to name mapping. TMDb exposes this via /genre/movie/list and /genre/tv/list,
/// but the lists have been stable for years and baking them in removes two network calls and a
/// failure mode from every cold lookup.
/// </summary>
public static class TmdbGenres
{
    /// <summary>TMDb genre id for "Talk".</summary>
    public const int Talk = 10767;

    /// <summary>TMDb genre id for "News".</summary>
    public const int News = 10763;

    /// <summary>TMDb genre id for "Reality".</summary>
    public const int Reality = 10764;

    /// <summary>Genre ids treated as chat-show noise rather than a real credit.</summary>
    public static readonly ImmutableHashSet<int> NoiseGenres = ImmutableHashSet.Create(Talk, News, Reality);

    private static readonly Dictionary<int, string> MovieGenres = new()
    {
        [28] = "Action",
        [12] = "Adventure",
        [16] = "Animation",
        [35] = "Comedy",
        [80] = "Crime",
        [99] = "Documentary",
        [18] = "Drama",
        [10751] = "Family",
        [14] = "Fantasy",
        [36] = "History",
        [27] = "Horror",
        [10402] = "Music",
        [9648] = "Mystery",
        [10749] = "Romance",
        [878] = "Science Fiction",
        [10770] = "TV Movie",
        [53] = "Thriller",
        [10752] = "War",
        [37] = "Western",
    };

    private static readonly Dictionary<int, string> TvGenres = new()
    {
        [10759] = "Action & Adventure",
        [16] = "Animation",
        [35] = "Comedy",
        [80] = "Crime",
        [99] = "Documentary",
        [18] = "Drama",
        [10751] = "Family",
        [10762] = "Kids",
        [9648] = "Mystery",
        [10763] = "News",
        [10764] = "Reality",
        [10765] = "Sci-Fi & Fantasy",
        [10766] = "Soap",
        [10767] = "Talk",
        [10768] = "War & Politics",
        [37] = "Western",
    };

    /// <summary>
    /// Resolves TMDb genre ids to display names, silently skipping ids TMDb has since added.
    /// </summary>
    /// <param name="kind">Whether to use the film or television genre list.</param>
    /// <param name="ids">The genre ids from the credit.</param>
    /// <returns>Resolved genre names, in the order given.</returns>
    public static IReadOnlyList<string> Resolve(MediaKind kind, IEnumerable<int>? ids)
    {
        if (ids is null)
        {
            return System.Array.Empty<string>();
        }

        var table = kind == MediaKind.Series ? TvGenres : MovieGenres;
        var result = new List<string>();
        foreach (var id in ids)
        {
            if (table.TryGetValue(id, out var name) && !result.Contains(name))
            {
                result.Add(name);
            }
        }

        return result;
    }
}
