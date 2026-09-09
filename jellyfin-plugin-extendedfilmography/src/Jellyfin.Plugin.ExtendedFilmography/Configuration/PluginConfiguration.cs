using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ExtendedFilmography.Configuration;

/// <summary>
/// How poster and backdrop images for synthetic items are delivered to clients.
/// </summary>
public enum ImageDeliveryMode
{
    /// <summary>Answer with a 302 to image.tmdb.org. Cheapest; requires the client to follow redirects.</summary>
    Redirect = 0,

    /// <summary>Fetch the bytes server-side and stream them to the client. Use if a client will not follow the redirect.</summary>
    Proxy = 1,
}

/// <summary>
/// User-editable settings, surfaced on the plugin's dashboard page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the completed defaults migration version.</summary>
    public int DefaultsVersion { get; set; }

    /// <summary>Migrates original defaults without replacing customized values.</summary>
    public bool MigrateDefaults()
    {
        if (DefaultsVersion >= 1) return false;
        if (MaxItems == 20) MaxItems = 50;
        if (MinVoteCount == 100) MinVoteCount = 10;
        if (MaxBillingOrder == 10) MaxBillingOrder = 0;
        if (MinEpisodeCountWithoutOrder == 5) MinEpisodeCountWithoutOrder = 1;
        DefaultsVersion = 1;
        return true;
    }

    /// <summary>Gets or sets a value indicating whether the plugin does anything at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the TMDb API key (v3 auth). Required.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the metadata language passed to TMDb, e.g. "en-US" or "sv-SE".</summary>
    public string TmdbLanguage { get; set; } = "en-US";

    /// <summary>Gets or sets how many external titles to add per person.</summary>
    public int MaxItems { get; set; } = 50;

    /// <summary>Gets or sets the weight of TMDb popularity in the rank score.</summary>
    public double PopularityWeight { get; set; } = 0.6;

    /// <summary>Gets or sets the weight of TMDb rating in the rank score.</summary>
    public double RatingWeight { get; set; } = 0.4;

    /// <summary>Gets or sets the minimum TMDb vote count for a credit to qualify.</summary>
    public int MinVoteCount { get; set; } = 10;

    /// <summary>Gets or sets the highest billing order accepted, e.g. 10 = top ten billed cast.</summary>
    public int MaxBillingOrder { get; set; } = 0;

    /// <summary>
    /// Gets or sets the episode count that lets a TV credit through when TMDb gives it no
    /// billing order. Guest spots typically have one or two.
    /// </summary>
    public int MinEpisodeCountWithoutOrder { get; set; } = 1;

    /// <summary>Gets or sets a value indicating whether films are included.</summary>
    public bool IncludeMovies { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether series are included.</summary>
    public bool IncludeSeries { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether crew credits are ignored.</summary>
    public bool ActingCreditsOnly { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether "as Self" appearances are dropped.</summary>
    public bool ExcludeSelfAppearances { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether talk shows, news and reality TV are dropped.</summary>
    public bool ExcludeTalkNewsReality { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether titles already in the library are dropped.</summary>
    public bool HideItemsAlreadyInLibrary { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether unreleased titles are dropped.</summary>
    public bool ExcludeUnreleased { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether a suffix is appended to external titles.</summary>
    public bool MarkExternalTitles { get; set; }

    /// <summary>Gets or sets the suffix used when <see cref="MarkExternalTitles"/> is on.</summary>
    public string ExternalTitleSuffix { get; set; } = " (not in library)";

    /// <summary>Gets or sets how images are delivered.</summary>
    public ImageDeliveryMode ImageMode { get; set; } = ImageDeliveryMode.Redirect;

    /// <summary>
    /// Gets or sets the base URL of a Jellyseerr / Overseerr instance, e.g.
    /// "https://requests.example.com". When set, each external title carries a request link.
    /// </summary>
    public string SeerrBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the request link is also appended to the synopsis,
    /// for clients that do not render external links.
    /// </summary>
    public bool AppendRequestLinkToOverview { get; set; }

    /// <summary>Gets or sets how long a person's filmography is cached, in days.</summary>
    public int CacheTtlDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets how long a person-page request will wait for a cold TMDb lookup, in
    /// milliseconds. On timeout the page renders without the extra titles and the lookup
    /// finishes in the background, so the next visit is instant.
    /// </summary>
    public int LookupTimeoutMs { get; set; } = 4000;

    /// <summary>Gets or sets a value indicating whether per-request decisions are logged at debug level.</summary>
    public bool VerboseLogging { get; set; }
}
