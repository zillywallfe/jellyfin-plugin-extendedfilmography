using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.ExtendedFilmography.Configuration;
using Jellyfin.Plugin.ExtendedFilmography.Model;
using Jellyfin.Plugin.ExtendedFilmography.Services;

namespace Jellyfin.Plugin.ExtendedFilmography.Middleware;

/// <summary>
/// Constraints taken from the original request, which the injected items must respect.
/// </summary>
/// <param name="StartIndex">The requested start index, if the client is paging.</param>
/// <param name="Limit">The requested page size, if any.</param>
/// <param name="IncludeItemTypes">Item types the client asked for, if it narrowed the query.</param>
public readonly record struct InjectionLimits(
    int? StartIndex,
    int? Limit,
    IReadOnlySet<string>? IncludeItemTypes);

/// <summary>
/// Appends synthetic items to a Jellyfin <c>QueryResult&lt;BaseItemDto&gt;</c> response body.
/// Pure: bytes in, bytes out.
/// </summary>
public static class ItemsResponseInjector
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Appends entries to a serialised item query result.
    /// </summary>
    /// <param name="json">The original response body, already decompressed.</param>
    /// <param name="entries">Candidate entries, best first.</param>
    /// <param name="config">Current plugin settings.</param>
    /// <param name="serverId">The Jellyfin server id.</param>
    /// <param name="limits">Constraints from the original request.</param>
    /// <param name="result">Receives the rewritten body, or the original on any refusal.</param>
    /// <param name="added">Receives the number of items appended.</param>
    /// <returns><c>true</c> if the body was rewritten.</returns>
    public static bool TryInject(
        byte[] json,
        IReadOnlyList<FilmographyEntry> entries,
        PluginConfiguration config,
        string serverId,
        InjectionLimits limits,
        out byte[] result,
        out int added)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(config);

        result = json;
        added = 0;

        if (entries.Count == 0)
        {
            return false;
        }

        // A client asking for page two must not be handed the extras again.
        if (limits.StartIndex is > 0)
        {
            return false;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (root is not JsonObject envelope
            || !envelope.TryGetPropertyValue("Items", out var itemsNode)
            || itemsNode is not JsonArray items)
        {
            return false;
        }

        // If the page came back full, there are more pages behind it. Appending here would
        // shift the client's paging window, so leave it alone.
        if (limits.Limit is int limit && items.Count >= limit)
        {
            return false;
        }

        var allowance = limits.Limit is int lim ? Math.Max(0, lim - items.Count) : int.MaxValue;
        if (allowance == 0)
        {
            return false;
        }

        var existingIds = CollectExistingIds(items);
        var appended = new List<JsonObject>();

        foreach (var entry in entries)
        {
            if (appended.Count >= allowance)
            {
                break;
            }

            if (!TypeAllowed(entry.Kind, limits.IncludeItemTypes))
            {
                continue;
            }

            var id = SyntheticId.ToApiString(SyntheticId.Create(entry.Kind, entry.TmdbId));
            if (existingIds.Contains(id))
            {
                continue;
            }

            appended.Add(DtoBuilder.Build(entry, config, serverId));
            existingIds.Add(id);
        }

        if (appended.Count == 0)
        {
            return false;
        }

        foreach (var item in appended)
        {
            items.Add(item);
        }

        if (envelope.TryGetPropertyValue("TotalRecordCount", out var totalNode)
            && totalNode is JsonValue totalValue
            && totalValue.TryGetValue<int>(out var total))
        {
            envelope["TotalRecordCount"] = total + appended.Count;
        }
        else
        {
            envelope["TotalRecordCount"] = items.Count;
        }

        result = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
        added = appended.Count;
        return true;
    }

    /// <summary>
    /// Builds an empty <c>QueryResult</c> envelope, used to answer child queries against
    /// synthetic items.
    /// </summary>
    /// <returns>The serialised envelope.</returns>
    public static byte[] EmptyQueryResult()
        => System.Text.Encoding.UTF8.GetBytes("{\"Items\":[],\"TotalRecordCount\":0,\"StartIndex\":0}");

    /// <summary>
    /// Builds an empty JSON array, used to answer list endpoints against synthetic items.
    /// </summary>
    /// <returns>The serialised array.</returns>
    public static byte[] EmptyArray() => System.Text.Encoding.UTF8.GetBytes("[]");

    /// <summary>
    /// Serialises a single item object.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The serialised object.</returns>
    public static byte[] Serialize(JsonObject item)
        => JsonSerializer.SerializeToUtf8Bytes(item, SerializerOptions);

    private static bool TypeAllowed(MediaKind kind, IReadOnlySet<string>? includeItemTypes)
    {
        if (includeItemTypes is null || includeItemTypes.Count == 0)
        {
            return true;
        }

        return includeItemTypes.Contains(kind == MediaKind.Series ? "Series" : "Movie");
    }

    private static HashSet<string> CollectExistingIds(JsonArray items)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in items)
        {
            if (node is JsonObject obj
                && obj.TryGetPropertyValue("Id", out var idNode)
                && idNode is JsonValue idValue
                && idValue.TryGetValue<string>(out var id)
                && !string.IsNullOrEmpty(id))
            {
                ids.Add(id.Replace("-", string.Empty, StringComparison.Ordinal));
            }
        }

        return ids;
    }
}
