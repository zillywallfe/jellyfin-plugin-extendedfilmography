namespace Jellyfin.Plugin.ExtendedFilmography.Model;

/// <summary>
/// The kind of title a synthetic filmography entry represents.
/// The numeric values are persisted inside synthetic GUIDs, so they must never change.
/// </summary>
public enum MediaKind
{
    /// <summary>Unknown / unsupported.</summary>
    Unknown = 0,

    /// <summary>A film. Maps to Jellyfin item type "Movie".</summary>
    Movie = 1,

    /// <summary>A television series. Maps to Jellyfin item type "Series".</summary>
    Series = 2,
}
