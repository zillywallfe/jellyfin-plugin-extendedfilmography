using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExtendedFilmography.Abstractions;
using Jellyfin.Plugin.ExtendedFilmography.Configuration;
using Jellyfin.Plugin.ExtendedFilmography.Model;
using Jellyfin.Plugin.ExtendedFilmography.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExtendedFilmography.Middleware;

/// <summary>
/// The whole trick. Sits at the front of Jellyfin's HTTP pipeline and:
/// <list type="bullet">
///   <item>appends external titles to the person page's item query;</item>
///   <item>serves detail pages, posters and backdrops for those titles;</item>
///   <item>absorbs every other request a client might make about them.</item>
/// </list>
/// Nothing is written to the library database and nothing is written to disk.
/// </summary>
public sealed class ExtendedFilmographyMiddleware
{
    /// <summary>Name of the named <see cref="HttpClient"/> used for image proxying.</summary>
    public const string ImageHttpClientName = "ExtendedFilmographyImages";

    private const string TmdbImageBase = "https://image.tmdb.org/t/p/";

    private readonly RequestDelegate _next;
    private readonly ILogger<ExtendedFilmographyMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExtendedFilmographyMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next delegate in the pipeline.</param>
    /// <param name="logger">The logger.</param>
    public ExtendedFilmographyMiddleware(RequestDelegate next, ILogger<ExtendedFilmographyMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Handles one request.
    /// </summary>
    /// <param name="context">The request context.</param>
    /// <param name="provider">The filmography provider, resolved per request.</param>
    /// <param name="httpClientFactory">Factory for the image proxy client.</param>
    /// <returns>A task.</returns>
    public async Task InvokeAsync(
        HttpContext context,
        IFilmographyProvider provider,
        IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(provider);

        var config = provider.Configuration;

        if (!config.Enabled || !HttpMethods.IsGet(context.Request.Method))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        RouteMatch match;
        try
        {
            match = RouteMatcher.Match(context.Request.Path, context.Request.Query);
        }
        catch (Exception ex)
        {
            // Routing must never be the reason a request fails.
            _logger.LogDebug(ex, "Route matching threw for {Path}; passing through", context.Request.Path);
            await _next(context).ConfigureAwait(false);
            return;
        }

        switch (match.Kind)
        {
            case RouteKind.PersonItems:
                await HandlePersonItemsAsync(context, provider, match).ConfigureAwait(false);
                return;

            case RouteKind.SyntheticItem:
                await HandleSyntheticItemAsync(context, provider, match).ConfigureAwait(false);
                return;

            case RouteKind.SyntheticImage:
                await HandleSyntheticImageAsync(context, provider, httpClientFactory, match).ConfigureAwait(false);
                return;

            case RouteKind.EmptyQueryResult:
                await WriteJsonAsync(context, ItemsResponseInjector.EmptyQueryResult()).ConfigureAwait(false);
                return;

            case RouteKind.EmptyArray:
                await WriteJsonAsync(context, ItemsResponseInjector.EmptyArray()).ConfigureAwait(false);
                return;

            case RouteKind.NotFound:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;

            default:
                await _next(context).ConfigureAwait(false);
                return;
        }
    }

    private async Task HandlePersonItemsAsync(HttpContext context, IFilmographyProvider provider, RouteMatch match)
    {
        var config = provider.Configuration;

        // Start the TMDb lookup before handing off, so it overlaps Jellyfin's own database
        // query instead of adding to it. SafeLookupAsync never throws, so the task can be
        // awaited later without risking an unobserved exception on the pass-through paths.
        var entriesTask = SafeLookupAsync(provider, match.PersonId, context.RequestAborted);

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();

        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;

        // Anything that is not a plain successful JSON body goes back exactly as it came.
        if (context.Response.StatusCode != StatusCodes.Status200OK || !IsJson(context.Response.ContentType))
        {
            await CopyThroughAsync(buffer, originalBody, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var encoding = ResponseCodec.Parse(context.Response.Headers.ContentEncoding);
        if (encoding == BodyEncoding.Unsupported)
        {
            await CopyThroughAsync(buffer, originalBody, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        byte[] rewritten;
        int added;
        try
        {
            var raw = buffer.ToArray();
            var plain = await ResponseCodec.DecodeAsync(raw, encoding, context.RequestAborted).ConfigureAwait(false);

            var entries = await entriesTask.ConfigureAwait(false);

            var limits = new InjectionLimits(
                RouteMatcher.GetIntParam(context.Request.Query, "startIndex"),
                RouteMatcher.GetIntParam(context.Request.Query, "limit"),
                ParseIncludeItemTypes(context.Request.Query));

            if (!ItemsResponseInjector.TryInject(plain, entries, config, provider.ServerId, limits, out var modified, out added))
            {
                await CopyThroughAsync(buffer, originalBody, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            rewritten = await ResponseCodec.EncodeAsync(modified, encoding, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fail open: a broken lookup must degrade to stock Jellyfin behaviour, never to a
            // broken person page.
            _logger.LogWarning(ex, "Failed to extend person {PersonId}; serving the original response", match.PersonId);
            await CopyThroughAsync(buffer, originalBody, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (config.VerboseLogging)
        {
            _logger.LogDebug("Appended {Count} external titles for person {PersonId}", added, match.PersonId);
        }

        // Nothing has reached the wire yet - the inner pipeline wrote into our buffer - but guard
        // anyway, because setting a header on a started response throws.
        if (!context.Response.HasStarted)
        {
            context.Response.ContentLength = rewritten.Length;
        }

        await originalBody.WriteAsync(rewritten, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task HandleSyntheticItemAsync(HttpContext context, IFilmographyProvider provider, RouteMatch match)
    {
        var entry = await provider.GetEntryAsync(match.ItemKind, match.TmdbId, context.RequestAborted).ConfigureAwait(false);
        if (entry is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var dto = DtoBuilder.Build(entry, provider.Configuration, provider.ServerId);
        await WriteJsonAsync(context, ItemsResponseInjector.Serialize(dto)).ConfigureAwait(false);
    }

    private async Task HandleSyntheticImageAsync(
        HttpContext context,
        IFilmographyProvider provider,
        IHttpClientFactory httpClientFactory,
        RouteMatch match)
    {
        var entry = await provider.GetEntryAsync(match.ItemKind, match.TmdbId, context.RequestAborted).ConfigureAwait(false);
        var path = SelectImagePath(entry, match.ImageType);
        if (path is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var size = SelectImageSize(context.Request.Query, match.ImageType);
        var url = TmdbImageBase + size + path;

        // A TMDb poster path is immutable, so this can be cached hard.
        context.Response.Headers.CacheControl = "public, max-age=2592000";

        if (provider.Configuration.ImageMode == ImageDeliveryMode.Redirect)
        {
            context.Response.Redirect(url, permanent: false);
            return;
        }

        try
        {
            var client = httpClientFactory.CreateClient(ImageHttpClientName);
            using var upstream = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted)
                .ConfigureAwait(false);

            if (!upstream.IsSuccessStatusCode)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
            if (upstream.Content.Headers.ContentLength is long length)
            {
                context.Response.ContentLength = length;
            }

            await upstream.Content.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to proxy image {Url}", url);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
            }
        }
    }

    /// <summary>
    /// Picks the TMDb path for the requested image type.
    /// </summary>
    /// <param name="entry">The entry, possibly null.</param>
    /// <param name="imageType">The Jellyfin image type name.</param>
    /// <returns>The TMDb path, or <c>null</c> when there is nothing to serve.</returns>
    public static string? SelectImagePath(FilmographyEntry? entry, string? imageType)
    {
        if (entry is null)
        {
            return null;
        }

        return imageType?.ToLowerInvariant() switch
        {
            "backdrop" => entry.BackdropPath,
            "thumb" => entry.BackdropPath ?? entry.PosterPath,
            "banner" => entry.BackdropPath ?? entry.PosterPath,
            "primary" => entry.PosterPath,
            null => entry.PosterPath,
            _ => null,
        };
    }

    /// <summary>
    /// Translates the width the client asked for into a TMDb size bucket.
    /// </summary>
    /// <param name="query">The request query.</param>
    /// <param name="imageType">The Jellyfin image type name.</param>
    /// <returns>A TMDb size segment such as "w500".</returns>
    public static string SelectImageSize(IQueryCollection query, string? imageType)
    {
        var isBackdrop = string.Equals(imageType, "Backdrop", StringComparison.OrdinalIgnoreCase);

        var requested = RouteMatcher.GetIntParam(query, "fillWidth")
                        ?? RouteMatcher.GetIntParam(query, "maxWidth")
                        ?? RouteMatcher.GetIntParam(query, "width");

        if (isBackdrop)
        {
            return requested switch
            {
                <= 300 => "w300",
                <= 780 => "w780",
                _ => "w1280",
            };
        }

        return requested switch
        {
            null => "w500",
            <= 185 => "w185",
            <= 342 => "w342",
            <= 500 => "w500",
            _ => "w780",
        };
    }

    /// <summary>
    /// Parses the client's includeItemTypes filter, if it set one.
    /// </summary>
    /// <param name="query">The request query.</param>
    /// <returns>The set of requested types, or <c>null</c>.</returns>
    public static IReadOnlySet<string>? ParseIncludeItemTypes(IQueryCollection query)
    {
        var raw = RouteMatcher.GetParam(query, "includeItemTypes");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var set = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return set.Count == 0 ? null : set;
    }

    private async Task<IReadOnlyList<FilmographyEntry>> SafeLookupAsync(
        IFilmographyProvider provider,
        Guid personId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.GetForPersonAsync(personId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<FilmographyEntry>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Filmography lookup failed for person {PersonId}", personId);
            return Array.Empty<FilmographyEntry>();
        }
    }

    private static bool IsJson(string? contentType)
        => contentType is not null
           && contentType.Contains("json", StringComparison.OrdinalIgnoreCase);

    private static async Task CopyThroughAsync(MemoryStream buffer, Stream destination, CancellationToken cancellationToken)
    {
        buffer.Position = 0;
        await buffer.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(HttpContext context, byte[] payload)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = payload.Length;
        await context.Response.Body.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
    }
}
