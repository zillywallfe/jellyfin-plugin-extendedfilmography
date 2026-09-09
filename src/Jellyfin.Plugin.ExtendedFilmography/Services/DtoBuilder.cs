using System;
using System.Globalization;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.ExtendedFilmography.Configuration;
using Jellyfin.Plugin.ExtendedFilmography.Model;

namespace Jellyfin.Plugin.ExtendedFilmography.Services;

/// <summary>
/// Builds the JSON object a Jellyfin client expects for one library item.
/// </summary>
/// <remarks>
/// This deliberately emits <c>JsonObject</c> rather than Jellyfin's <c>BaseItemDto</c>. The DTO
/// class changes shape between server releases; the wire format is far more stable, and
/// hand-writing it means the plugin never has to be recompiled just because a property was
/// added upstream.
/// </remarks>
public static class DtoBuilder
{
    /// <summary>Tag applied to every synthetic item, so they are trivially identifiable.</summary>
    public const string Tag = "extended-filmography";

    /// <summary>
    /// Image tag reported for synthetic items. Clients use it purely as a cache key, and it is
    /// constant because a TMDb poster path never changes for a given title.
    /// </summary>
    public const string ImageTag = "xfilm1";

    /// <summary>Standard 2:3 poster ratio.</summary>
    private const double PosterAspectRatio = 2.0 / 3.0;

    /// <summary>
    /// Renders a filmography entry as a Jellyfin item object.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="serverId">The Jellyfin server id, as clients expect on every item.</param>
    /// <returns>The item object, ready to be appended to an Items array.</returns>
    public static JsonObject Build(FilmographyEntry entry, PluginConfiguration config, string serverId)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(config);

        var isSeries = entry.Kind == MediaKind.Series;
        var id = SyntheticId.ToApiString(SyntheticId.Create(entry.Kind, entry.TmdbId));

        var name = entry.Name;
        if (config.MarkExternalTitles && !string.IsNullOrEmpty(config.ExternalTitleSuffix))
        {
            name += config.ExternalTitleSuffix;
        }

        var overview = entry.Overview;
        if (config.AppendRequestLinkToOverview)
        {
            var requestUrl = BuildSeerrUrl(entry, config);
            if (requestUrl is not null)
            {
                overview = string.IsNullOrEmpty(overview)
                    ? "Request: " + requestUrl
                    : overview + "\n\nRequest: " + requestUrl;
            }
        }

        var item = new JsonObject
        {
            ["Name"] = name,
            ["ServerId"] = serverId,
            ["Id"] = id,
            ["Type"] = isSeries ? "Series" : "Movie",

            // Virtual is the same LocationType Jellyfin already uses for missing episodes, so
            // clients that grey those out will grey these out too, and will not offer playback.
            ["LocationType"] = "Virtual",
            ["MediaType"] = isSeries ? "Unknown" : "Video",
            ["IsFolder"] = isSeries,
            ["PlayAccess"] = "None",

            ["CanDelete"] = false,
            ["CanDownload"] = false,
            ["SupportsSync"] = false,
            ["EnableMediaSourceDisplay"] = false,

            ["Overview"] = overview,
            ["Taglines"] = new JsonArray(),
            ["Genres"] = ToArray(entry.Genres),
            ["GenreItems"] = BuildGenreItems(entry),
            ["Studios"] = new JsonArray(),
            ["People"] = new JsonArray(),
            ["Tags"] = new JsonArray(Tag),

            ["ChildCount"] = isSeries ? JsonValue.Create(0) : null,
            ["LocalTrailerCount"] = 0,
            ["SpecialFeatureCount"] = 0,

            ["ProviderIds"] = new JsonObject { ["Tmdb"] = entry.TmdbId.ToString(CultureInfo.InvariantCulture) },
            ["ExternalUrls"] = BuildExternalUrls(entry, config),

            ["PrimaryImageAspectRatio"] = PosterAspectRatio,
            ["ImageTags"] = BuildImageTags(entry),
            ["BackdropImageTags"] = entry.BackdropPath is null ? new JsonArray() : new JsonArray(ImageTag),
            ["ImageBlurHashes"] = new JsonObject(),

            ["UserData"] = BuildUserData(entry),
        };

        if (entry.OriginalTitle is not null)
        {
            item["OriginalTitle"] = entry.OriginalTitle;
        }

        if (entry.PremiereDate is DateTime premiere)
        {
            item["PremiereDate"] = premiere.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
            item["ProductionYear"] = premiere.Year;
            item["EndDate"] = null;
        }

        if (entry.CommunityRating is double rating)
        {
            item["CommunityRating"] = (float)rating;
        }

        if (isSeries)
        {
            item["Status"] = null;
            item["AirDays"] = new JsonArray();
        }
        else
        {
            item["MediaSources"] = new JsonArray();
            item["MediaStreams"] = new JsonArray();
        }

        return item;
    }

    /// <summary>
    /// Builds the Jellyseerr / Overseerr request URL for an entry.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <returns>The URL, or <c>null</c> when no Seerr instance is configured.</returns>
    public static string? BuildSeerrUrl(FilmographyEntry entry, PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(config.SeerrBaseUrl))
        {
            return null;
        }

        var root = config.SeerrBaseUrl.Trim().TrimEnd('/');
        var segment = entry.Kind == MediaKind.Series ? "tv" : "movie";
        return string.Create(CultureInfo.InvariantCulture, $"{root}/{segment}/{entry.TmdbId}");
    }

    /// <summary>
    /// Builds the public TMDb page URL for an entry.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <returns>The URL.</returns>
    public static string BuildTmdbUrl(FilmographyEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var segment = entry.Kind == MediaKind.Series ? "tv" : "movie";
        return string.Create(CultureInfo.InvariantCulture, $"https://www.themoviedb.org/{segment}/{entry.TmdbId}");
    }

    private static JsonArray BuildExternalUrls(FilmographyEntry entry, PluginConfiguration config)
    {
        var urls = new JsonArray();

        var seerr = BuildSeerrUrl(entry, config);
        if (seerr is not null)
        {
            urls.Add(new JsonObject { ["Name"] = "Request on Jellyseerr", ["Url"] = seerr });
        }

        urls.Add(new JsonObject { ["Name"] = "TheMovieDb", ["Url"] = BuildTmdbUrl(entry) });
        return urls;
    }

    private static JsonObject BuildImageTags(FilmographyEntry entry)
    {
        var tags = new JsonObject();
        if (entry.PosterPath is not null)
        {
            tags["Primary"] = ImageTag;
        }

        return tags;
    }

    private static JsonArray BuildGenreItems(FilmographyEntry entry)
    {
        var items = new JsonArray();
        foreach (var genre in entry.Genres)
        {
            items.Add(new JsonObject { ["Name"] = genre, ["Id"] = Guid.Empty.ToString("N", CultureInfo.InvariantCulture) });
        }

        return items;
    }

    private static JsonObject BuildUserData(FilmographyEntry entry) => new()
    {
        ["PlaybackPositionTicks"] = 0,
        ["PlayCount"] = 0,
        ["IsFavorite"] = false,
        ["Played"] = false,
        ["Key"] = "xfilm-" + entry.Key.Replace(':', '-'),
    };

    private static JsonArray ToArray(System.Collections.Generic.IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
