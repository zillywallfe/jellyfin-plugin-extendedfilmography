using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ExtendedFilmography.Configuration;
using Jellyfin.Plugin.ExtendedFilmography.Model;

namespace Jellyfin.Plugin.ExtendedFilmography.Services;

/// <summary>
/// Turns a raw TMDb credit list into the ranked shortlist shown on the person page.
/// Pure and side-effect free, so it can be exercised without a server.
/// </summary>
public static class CreditRanker
{
    /// <summary>
    /// Matches credits where the person appears as themselves. TMDb is inconsistent here:
    /// "Self", "Self - Host", "Himself", "Herself (archive footage)", "Themselves" all occur.
    /// </summary>
    private static readonly Regex SelfPattern = new(
        @"^\s*(self|him\s*self|her\s*self|them\s*selves|themself)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Popularity is unbounded and heavily skewed, so it is compressed logarithmically and
    /// clamped. A popularity of 1000 or more scores a full 1.0 on that axis.
    /// </summary>
    private const double PopularityLogCeiling = 3.0;

    /// <summary>
    /// Filters and ranks a person's credits.
    /// </summary>
    /// <param name="credits">The parsed TMDb combined_credits payload.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="libraryKeys">
    /// Keys (see <see cref="FilmographyEntry.BuildKey"/>) of titles already in the library, or
    /// <c>null</c> to skip the library check.
    /// </param>
    /// <param name="now">The current date, injected so the unreleased filter is testable.</param>
    /// <returns>The top <see cref="PluginConfiguration.MaxItems"/> entries, best first.</returns>
    public static IReadOnlyList<FilmographyEntry> Rank(
        TmdbCombinedCredits credits,
        PluginConfiguration config,
        IReadOnlySet<string>? libraryKeys,
        DateTime now,
        bool limitResults = true)
    {
        ArgumentNullException.ThrowIfNull(credits);
        ArgumentNullException.ThrowIfNull(config);

        var source = new List<TmdbCredit>(credits.Cast);
        if (!config.ActingCreditsOnly)
        {
            source.AddRange(credits.Crew);
        }

        // TMDb lists a title once per character or per job, so the same show can appear
        // several times. Keep the most prominent occurrence of each title.
        var best = new Dictionary<string, FilmographyEntry>(StringComparer.Ordinal);

        foreach (var credit in source)
        {
            var entry = Convert(credit, config, libraryKeys, now);
            if (entry is null)
            {
                continue;
            }

            if (!best.TryGetValue(entry.Key, out var existing) || entry.Score > existing.Score)
            {
                best[entry.Key] = entry;
            }
        }

        var take = config.MaxItems > 0 ? config.MaxItems : 20;

        return best.Values
            .OrderByDescending(e => e.Score)
            .ThenByDescending(e => e.VoteCount)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limitResults ? take : int.MaxValue)
            .ToList();
    }

    /// <summary>
    /// Applies every filter to a single credit and computes its score.
    /// </summary>
    /// <param name="credit">The credit.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="libraryKeys">Keys of titles already in the library, or <c>null</c>.</param>
    /// <param name="now">The current date.</param>
    /// <returns>The entry, or <c>null</c> if the credit was filtered out.</returns>
    public static FilmographyEntry? Convert(
        TmdbCredit credit,
        PluginConfiguration config,
        IReadOnlySet<string>? libraryKeys,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(credit);
        ArgumentNullException.ThrowIfNull(config);

        var kind = credit.Kind;
        if (kind == MediaKind.Unknown)
        {
            return null;
        }

        if (kind == MediaKind.Movie && !config.IncludeMovies)
        {
            return null;
        }

        if (kind == MediaKind.Series && !config.IncludeSeries)
        {
            return null;
        }

        if (credit.Id <= 0 || credit.Adult)
        {
            return null;
        }

        var name = credit.DisplayName;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (credit.VoteCount < config.MinVoteCount)
        {
            return null;
        }

        if (config.ExcludeSelfAppearances
            && !string.IsNullOrWhiteSpace(credit.Character)
            && SelfPattern.IsMatch(credit.Character))
        {
            return null;
        }

        if (config.ExcludeTalkNewsReality && credit.GenreIds.Any(TmdbGenres.NoiseGenres.Contains))
        {
            return null;
        }

        if (!PassesBillingFilter(credit, config))
        {
            return null;
        }

        var premiere = ParseDate(credit.DisplayDate);
        if (config.ExcludeUnreleased && (premiere is null || premiere > now))
        {
            return null;
        }

        var key = FilmographyEntry.BuildKey(kind, credit.Id);
        if (config.HideItemsAlreadyInLibrary && libraryKeys is not null && libraryKeys.Contains(key))
        {
            return null;
        }

        var originalTitle = credit.DisplayOriginalName;

        return new FilmographyEntry
        {
            TmdbId = credit.Id,
            Kind = kind,
            Name = name!,
            OriginalTitle = string.Equals(originalTitle, name, StringComparison.Ordinal) ? null : originalTitle,
            Overview = string.IsNullOrWhiteSpace(credit.Overview) ? null : credit.Overview,
            PosterPath = credit.PosterPath,
            BackdropPath = credit.BackdropPath,
            PremiereDate = premiere,
            CommunityRating = credit.VoteAverage > 0 ? Math.Round(credit.VoteAverage, 1) : null,
            VoteCount = credit.VoteCount,
            Popularity = credit.Popularity,
            Genres = TmdbGenres.Resolve(kind, credit.GenreIds),
            Character = string.IsNullOrWhiteSpace(credit.Character) ? null : credit.Character,
            Score = Score(credit, config),
        };
    }

    /// <summary>
    /// Computes the blended popularity/rating score in the range 0..1.
    /// </summary>
    /// <param name="credit">The credit.</param>
    /// <param name="config">Current plugin settings, supplying the two weights.</param>
    /// <returns>The score. Higher is better.</returns>
    public static double Score(TmdbCredit credit, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(credit);
        ArgumentNullException.ThrowIfNull(config);

        var popularityWeight = Math.Max(0, config.PopularityWeight);
        var ratingWeight = Math.Max(0, config.RatingWeight);
        var total = popularityWeight + ratingWeight;
        if (total <= 0)
        {
            popularityWeight = 0.6;
            ratingWeight = 0.4;
            total = 1.0;
        }

        var popularityAxis = Math.Clamp(Math.Log10(Math.Max(0, credit.Popularity) + 1) / PopularityLogCeiling, 0, 1);
        var ratingAxis = Math.Clamp(credit.VoteAverage / 10.0, 0, 1);

        return ((popularityWeight * popularityAxis) + (ratingWeight * ratingAxis)) / total;
    }

    /// <summary>Applies the current library snapshot before limiting cached candidates.</summary>
    public static IReadOnlyList<FilmographyEntry> SelectForLibrary(
        IReadOnlyList<FilmographyEntry> candidates, IReadOnlySet<string>? libraryKeys, int maxItems)
        => candidates.Where(entry => libraryKeys is null || !libraryKeys.Contains(entry.Key))
            .Take(Math.Clamp(maxItems, 1, 100)).ToList();

    private static bool PassesBillingFilter(TmdbCredit credit, PluginConfiguration config)
    {
        // A crew credit has no billing order and should not be judged by one.
        if (!string.IsNullOrWhiteSpace(credit.Job) && string.IsNullOrWhiteSpace(credit.Character))
        {
            return true;
        }

        if (config.MaxBillingOrder <= 0)
        {
            return true;
        }

        if (credit.Order.HasValue)
        {
            return credit.Order.Value < config.MaxBillingOrder;
        }

        // TMDb frequently omits `order` on television credits. Fall back to how much of the
        // show the person was actually in, which is the signal we wanted from billing anyway.
        return credit.EpisodeCount.HasValue
               && credit.EpisodeCount.Value >= config.MinEpisodeCountWithoutOrder;
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
