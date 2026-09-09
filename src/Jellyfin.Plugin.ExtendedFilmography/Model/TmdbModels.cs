using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ExtendedFilmography.Model;

/// <summary>
/// Response shape of TMDb's <c>/person/{id}/combined_credits</c> endpoint.
/// </summary>
public sealed class TmdbCombinedCredits
{
    /// <summary>Gets or sets the TMDb person id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the acting credits.</summary>
    [JsonPropertyName("cast")]
    public List<TmdbCredit> Cast { get; set; } = new();

    /// <summary>Gets or sets the crew credits (director, writer, producer, ...).</summary>
    [JsonPropertyName("crew")]
    public List<TmdbCredit> Crew { get; set; } = new();
}

/// <summary>
/// Response shape of TMDb's <c>/movie/{id}</c> and <c>/tv/{id}</c> endpoints. Only used as a
/// fallback when a client asks for a detail page the plugin has no cached credit for, which
/// happens after a server restart if the client kept the id.
/// </summary>
public sealed class TmdbTitle
{
    /// <summary>Gets or sets the TMDb id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the film title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the series name.</summary>
    [JsonPropertyName("name")]
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets the original-language film title.</summary>
    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; set; }

    /// <summary>Gets or sets the original-language series name.</summary>
    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }

    /// <summary>Gets or sets the synopsis.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the poster path.</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the backdrop path.</summary>
    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    /// <summary>Gets or sets the film release date.</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    /// <summary>Gets or sets the series first-air date.</summary>
    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    /// <summary>Gets or sets the average rating out of 10.</summary>
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    /// <summary>Gets or sets the number of votes.</summary>
    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    /// <summary>Gets or sets TMDb's popularity metric.</summary>
    [JsonPropertyName("popularity")]
    public double Popularity { get; set; }

    /// <summary>Gets or sets the full genre objects this endpoint returns.</summary>
    [JsonPropertyName("genres")]
    public List<TmdbGenre> Genres { get; set; } = new();

    /// <summary>Gets the title regardless of media type.</summary>
    public string? DisplayName => string.IsNullOrWhiteSpace(Title) ? SeriesName : Title;

    /// <summary>Gets the original-language title regardless of media type.</summary>
    public string? DisplayOriginalName => string.IsNullOrWhiteSpace(OriginalTitle) ? OriginalName : OriginalTitle;

    /// <summary>Gets the release date string regardless of media type.</summary>
    public string? DisplayDate => string.IsNullOrWhiteSpace(ReleaseDate) ? FirstAirDate : ReleaseDate;
}

/// <summary>
/// A genre as returned by TMDb's detail endpoints.
/// </summary>
public sealed class TmdbGenre
{
    /// <summary>Gets or sets the genre id.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the genre name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>
/// A single credit from TMDb's combined_credits response. The endpoint returns film and
/// television entries in the same array with differently named fields, so both spellings
/// are present here and normalised by the accessors.
/// </summary>
public sealed class TmdbCredit
{
    /// <summary>Gets or sets the TMDb id of the title.</summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>Gets or sets the discriminator, "movie" or "tv".</summary>
    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    /// <summary>Gets or sets the film title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>Gets or sets the series name.</summary>
    [JsonPropertyName("name")]
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets the original-language film title.</summary>
    [JsonPropertyName("original_title")]
    public string? OriginalTitle { get; set; }

    /// <summary>Gets or sets the original-language series name.</summary>
    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }

    /// <summary>Gets or sets the synopsis.</summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>Gets or sets the poster path.</summary>
    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; set; }

    /// <summary>Gets or sets the backdrop path.</summary>
    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; set; }

    /// <summary>Gets or sets the film release date, "yyyy-MM-dd".</summary>
    [JsonPropertyName("release_date")]
    public string? ReleaseDate { get; set; }

    /// <summary>Gets or sets the series first-air date, "yyyy-MM-dd".</summary>
    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; set; }

    /// <summary>Gets or sets the average rating out of 10.</summary>
    [JsonPropertyName("vote_average")]
    public double VoteAverage { get; set; }

    /// <summary>Gets or sets the number of votes.</summary>
    [JsonPropertyName("vote_count")]
    public int VoteCount { get; set; }

    /// <summary>Gets or sets TMDb's popularity metric.</summary>
    [JsonPropertyName("popularity")]
    public double Popularity { get; set; }

    /// <summary>Gets or sets the TMDb genre ids.</summary>
    [JsonPropertyName("genre_ids")]
    public List<int> GenreIds { get; set; } = new();

    /// <summary>Gets or sets the character played, for cast credits.</summary>
    [JsonPropertyName("character")]
    public string? Character { get; set; }

    /// <summary>Gets or sets the crew job, for crew credits.</summary>
    [JsonPropertyName("job")]
    public string? Job { get; set; }

    /// <summary>Gets or sets the billing order. Lower is more prominent. Absent on many TV credits.</summary>
    [JsonPropertyName("order")]
    public int? Order { get; set; }

    /// <summary>Gets or sets the number of episodes the person appeared in, for TV credits.</summary>
    [JsonPropertyName("episode_count")]
    public int? EpisodeCount { get; set; }

    /// <summary>Gets or sets a value indicating whether the title is adult content.</summary>
    [JsonPropertyName("adult")]
    public bool Adult { get; set; }

    /// <summary>Gets the title regardless of media type.</summary>
    public string? DisplayName => string.IsNullOrWhiteSpace(Title) ? SeriesName : Title;

    /// <summary>Gets the original-language title regardless of media type.</summary>
    public string? DisplayOriginalName => string.IsNullOrWhiteSpace(OriginalTitle) ? OriginalName : OriginalTitle;

    /// <summary>Gets the release date string regardless of media type.</summary>
    public string? DisplayDate => string.IsNullOrWhiteSpace(ReleaseDate) ? FirstAirDate : ReleaseDate;

    /// <summary>Gets the normalised media kind.</summary>
    public MediaKind Kind => MediaType switch
    {
        "movie" => MediaKind.Movie,
        "tv" => MediaKind.Series,
        _ => MediaKind.Unknown,
    };
}
