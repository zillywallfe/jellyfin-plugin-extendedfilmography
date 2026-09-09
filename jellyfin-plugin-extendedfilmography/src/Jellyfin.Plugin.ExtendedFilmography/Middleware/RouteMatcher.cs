using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ExtendedFilmography.Model;
using Jellyfin.Plugin.ExtendedFilmography.Services;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.ExtendedFilmography.Middleware;

/// <summary>
/// What the middleware should do with a request.
/// </summary>
public enum RouteKind
{
    /// <summary>Not our business. Pass straight through.</summary>
    None = 0,

    /// <summary>A person-page item query whose response should have external titles appended.</summary>
    PersonItems,

    /// <summary>A detail request for one of our synthetic items.</summary>
    SyntheticItem,

    /// <summary>A poster or backdrop request for one of our synthetic items.</summary>
    SyntheticImage,

    /// <summary>A child/related query against a synthetic item. Answer with an empty QueryResult.</summary>
    EmptyQueryResult,

    /// <summary>A related-list query against a synthetic item. Answer with an empty JSON array.</summary>
    EmptyArray,

    /// <summary>Anything else touching a synthetic item. Answer 404 rather than let it reach the library.</summary>
    NotFound,
}

/// <summary>
/// The outcome of matching one request.
/// </summary>
/// <param name="Kind">What to do.</param>
/// <param name="PersonId">The person being viewed, for <see cref="RouteKind.PersonItems"/>.</param>
/// <param name="ItemKind">The synthetic item's kind, where one was addressed.</param>
/// <param name="TmdbId">The synthetic item's TMDb id, where one was addressed.</param>
/// <param name="ImageType">The requested image type, for <see cref="RouteKind.SyntheticImage"/>.</param>
public readonly record struct RouteMatch(
    RouteKind Kind,
    Guid PersonId,
    MediaKind ItemKind,
    int TmdbId,
    string? ImageType)
{
    /// <summary>A match that means "do nothing".</summary>
    public static readonly RouteMatch NoMatch = new(RouteKind.None, Guid.Empty, Model.MediaKind.Unknown, 0, null);
}

/// <summary>
/// Classifies incoming requests. Pure: no I/O, no dependency on the plugin's state, so the whole
/// routing table can be exercised in isolation.
/// </summary>
public static class RouteMatcher
{
    private static readonly char[] Separator = { '/' };

    /// <summary>
    /// Related-item endpoints that must answer with a bare JSON array rather than a QueryResult.
    /// </summary>
    private static readonly HashSet<string> ArrayEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "LocalTrailers",
        "SpecialFeatures",
        "AdditionalParts",
        "AlternateSources",
    };

    /// <summary>
    /// Related-item endpoints that must answer with an empty QueryResult envelope.
    /// </summary>
    private static readonly HashSet<string> QueryResultEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "Similar",
        "Intros",
        "Seasons",
        "Episodes",
        "Recommendations",
    };

    /// <summary>
    /// Classifies a request.
    /// </summary>
    /// <param name="path">The request path.</param>
    /// <param name="query">The query string.</param>
    /// <returns>What the middleware should do.</returns>
    public static RouteMatch Match(PathString path, IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var raw = path.Value;
        if (string.IsNullOrEmpty(raw))
        {
            return RouteMatch.NoMatch;
        }

        var segments = raw.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return RouteMatch.NoMatch;
        }

        // Jellyfin can be hosted under a base path, so anchor on the first API segment we know
        // rather than assuming the route starts at index 0.
        var anchor = FindAnchor(segments);
        if (anchor < 0)
        {
            return RouteMatch.NoMatch;
        }

        var tail = segments.AsSpan(anchor);

        // /Users/{userId}/Items[/...] — normalise away the deprecated user-scoped prefix.
        if (tail.Length >= 3
            && tail[0].Equals("Users", StringComparison.OrdinalIgnoreCase)
            && tail[2].Equals("Items", StringComparison.OrdinalIgnoreCase))
        {
            tail = tail.Slice(2);
        }

        var head = tail[0];

        if (head.Equals("Items", StringComparison.OrdinalIgnoreCase))
        {
            return MatchItems(tail, query);
        }

        if (head.Equals("Shows", StringComparison.OrdinalIgnoreCase) && tail.Length >= 3)
        {
            if (SyntheticId.TryParseAndDecode(tail[1], out var kind, out var tmdbId))
            {
                return QueryResultEndpoints.Contains(tail[2])
                    ? new RouteMatch(RouteKind.EmptyQueryResult, Guid.Empty, kind, tmdbId, null)
                    : new RouteMatch(RouteKind.NotFound, Guid.Empty, kind, tmdbId, null);
            }

            return RouteMatch.NoMatch;
        }

        // /Videos/{id}/..., /Audio/{id}/... — playback. Refuse fast, never reach the library.
        if ((head.Equals("Videos", StringComparison.OrdinalIgnoreCase)
             || head.Equals("Audio", StringComparison.OrdinalIgnoreCase))
            && tail.Length >= 2
            && SyntheticId.TryParseAndDecode(tail[1], out var vKind, out var vId))
        {
            return new RouteMatch(RouteKind.NotFound, Guid.Empty, vKind, vId, null);
        }

        return RouteMatch.NoMatch;
    }

    /// <summary>
    /// Reads a query parameter without caring about the casing the client chose.
    /// Jellyfin's own clients are inconsistent between "personIds" and "PersonIds".
    /// </summary>
    /// <param name="query">The query collection.</param>
    /// <param name="name">The parameter name.</param>
    /// <returns>The first value, or <c>null</c>.</returns>
    public static string? GetParam(IQueryCollection query, string name)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.TryGetValue(name, out var exact))
        {
            return exact.Count > 0 ? exact[0] : null;
        }

        foreach (var pair in query)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value.Count > 0 ? pair.Value[0] : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Parses an integer query parameter.
    /// </summary>
    /// <param name="query">The query collection.</param>
    /// <param name="name">The parameter name.</param>
    /// <returns>The value, or <c>null</c> if absent or unparseable.</returns>
    public static int? GetIntParam(IQueryCollection query, string name)
    {
        var raw = GetParam(query, name);
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static RouteMatch MatchItems(ReadOnlySpan<string> tail, IQueryCollection query)
    {
        // /Items — a collection query.
        if (tail.Length == 1)
        {
            var parentId = GetParam(query, "parentId");
            if (parentId is not null && SyntheticId.TryParseAndDecode(parentId, out var pKind, out var pTmdbId))
            {
                return new RouteMatch(RouteKind.EmptyQueryResult, Guid.Empty, pKind, pTmdbId, null);
            }

            var personIds = GetParam(query, "personIds");
            if (string.IsNullOrEmpty(personIds))
            {
                return RouteMatch.NoMatch;
            }

            // Only a single-person query is a person page. A multi-person query is something
            // else (a filter, a search) and must not be touched.
            if (personIds.Contains(',', StringComparison.Ordinal) || !Guid.TryParse(personIds, out var personId))
            {
                return RouteMatch.NoMatch;
            }

            return new RouteMatch(RouteKind.PersonItems, personId, Model.MediaKind.Unknown, 0, null);
        }

        if (!SyntheticId.TryParseAndDecode(tail[1], out var kind, out var tmdbId))
        {
            return RouteMatch.NoMatch;
        }

        // /Items/{syntheticId}
        if (tail.Length == 2)
        {
            return new RouteMatch(RouteKind.SyntheticItem, Guid.Empty, kind, tmdbId, null);
        }

        // /Items/{syntheticId}/Images/{type}[/{index}]
        if (tail[2].Equals("Images", StringComparison.OrdinalIgnoreCase))
        {
            var imageType = tail.Length >= 4 ? tail[3] : "Primary";
            return new RouteMatch(RouteKind.SyntheticImage, Guid.Empty, kind, tmdbId, imageType);
        }

        if (ArrayEndpoints.Contains(tail[2]))
        {
            return new RouteMatch(RouteKind.EmptyArray, Guid.Empty, kind, tmdbId, null);
        }

        if (QueryResultEndpoints.Contains(tail[2]))
        {
            return new RouteMatch(RouteKind.EmptyQueryResult, Guid.Empty, kind, tmdbId, null);
        }

        // PlaybackInfo, ThemeMedia, Refresh, Download, ... — anything else that would otherwise
        // hit the library with an id it has never heard of.
        return new RouteMatch(RouteKind.NotFound, Guid.Empty, kind, tmdbId, null);
    }

    private static int FindAnchor(string[] segments)
    {
        for (var i = 0; i < segments.Length; i++)
        {
            var s = segments[i];
            if (s.Equals("Items", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Shows", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Videos", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Audio", StringComparison.OrdinalIgnoreCase)
                || s.Equals("Users", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
