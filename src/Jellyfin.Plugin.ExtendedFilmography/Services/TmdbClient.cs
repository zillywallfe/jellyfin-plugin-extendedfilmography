using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExtendedFilmography.Model;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExtendedFilmography.Services;

/// <summary>
/// The only outbound network dependency in the plugin: one TMDb call per person, cached for
/// weeks afterwards.
/// </summary>
public sealed class TmdbClient : IDisposable
{
    /// <summary>Name of the named <see cref="HttpClient"/> used for TMDb API calls.</summary>
    public const string HttpClientName = "ExtendedFilmographyTmdb";

    private const string ApiBase = "https://api.themoviedb.org/3/";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbClient> _logger;

    /// <summary>
    /// TMDb's documented ceiling is around 50 requests/second. The plugin will never come close,
    /// but a bounded gate keeps a cold cache on a large library from opening dozens of sockets
    /// at once.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(4, 4);

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TmdbClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public TmdbClient(IHttpClientFactory httpClientFactory, ILogger<TmdbClient> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Fetches a person's complete acting and crew credits.
    /// </summary>
    /// <param name="personTmdbId">The TMDb person id.</param>
    /// <param name="apiKey">The TMDb v3 API key.</param>
    /// <param name="language">The metadata language, e.g. "en-US".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The credits, or <c>null</c> if TMDb could not be reached or refused.</returns>
    public Task<TmdbCombinedCredits?> GetCombinedCreditsAsync(
        int personTmdbId,
        string apiKey,
        string language,
        CancellationToken cancellationToken)
        => GetAsync<TmdbCombinedCredits>(
            $"person/{personTmdbId}/combined_credits",
            apiKey,
            language,
            $"person {personTmdbId}",
            cancellationToken);

    /// <summary>
    /// Fetches a single title. Used only as a fallback when a client asks for a detail page the
    /// plugin has no cached credit for.
    /// </summary>
    /// <param name="kind">Film or series.</param>
    /// <param name="tmdbId">The TMDb id.</param>
    /// <param name="apiKey">The TMDb v3 API key.</param>
    /// <param name="language">The metadata language.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The title, or <c>null</c>.</returns>
    public Task<TmdbTitle?> GetTitleAsync(
        MediaKind kind,
        int tmdbId,
        string apiKey,
        string language,
        CancellationToken cancellationToken)
    {
        var segment = kind == MediaKind.Series ? "tv" : "movie";
        return GetAsync<TmdbTitle>(
            $"{segment}/{tmdbId}",
            apiKey,
            language,
            $"{segment} {tmdbId}",
            cancellationToken);
    }

    private async Task<T?> GetAsync<T>(
        string path,
        string apiKey,
        string language,
        string subject,
        CancellationToken cancellationToken)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("No TMDb API key configured; the plugin cannot look anything up");
            return null;
        }

        var url = string.Create(
            CultureInfo.InvariantCulture,
            $"{ApiBase}{path}?api_key={Uri.EscapeDataString(apiKey)}&language={Uri.EscapeDataString(string.IsNullOrWhiteSpace(language) ? "en-US" : language)}");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogError("TMDb rejected the configured API key");
                return null;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Something Jellyfin knows but TMDb does not. Normal; not worth a warning.
                _logger.LogDebug("TMDb has no {Subject}", subject);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "TMDb returned {StatusCode} for {Subject}",
                    (int)response.StatusCode,
                    subject);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonSerializer
                .DeserializeAsync<T>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "TMDb lookup failed for {Subject}", subject);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Dispose();
        _disposed = true;
    }
}
